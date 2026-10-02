using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

// Placeholder (until the hub): pick a main weapon, sub and special between matches. L / Start toggles it;
// it closes itself when a match starts. Choices apply at once and are remembered (PlayerLoadout).
public class LoadoutMenu : MonoBehaviour
{
    [SerializeField] GameObject screen;
    [SerializeField] Button[] weaponButtons; // in PlayerLoadout.Weapons order
    [SerializeField] Button[] subButtons;    // in SubType order
    [SerializeField] Button[] specialButtons; // in SpecialType order
    [SerializeField] TMP_Text weaponInfo, subInfo, specialInfo;
    [SerializeField] Button closeButton;
    [SerializeField] Color normalColour = new Color(0.2f, 0.2f, 0.26f, 1f);
    [SerializeField] Color selectedColour = new Color(1f, 0.82f, 0.25f, 1f);

    ControlLayer input;
    bool open;

    public bool IsOpen => open;
    public Button[] WeaponButtons => weaponButtons;
    public Button[] SubButtons => subButtons;
    public Button[] SpecialButtons => specialButtons;

    void Awake()
    {
        input = new ControlLayer();
        input.GameControl.Enable();
        input.GameControl.Loadout.performed += _ => SetOpen(!open);
        for (int i = 0; i < weaponButtons.Length; i++)
        {
            int index = i;
            weaponButtons[i].onClick.AddListener(() => { if (PlayerLoadout.Local != null) PlayerLoadout.Local.SetMainWeapon(index); Refresh(); });
        }
        for (int i = 0; i < subButtons.Length; i++)
        {
            SubType type = (SubType)i;
            subButtons[i].onClick.AddListener(() => { if (PlayerLoadout.Local != null) PlayerLoadout.Local.SetSub(type); Refresh(); });
        }
        for (int i = 0; i < specialButtons.Length; i++)
        {
            SpecialType type = (SpecialType)i;
            specialButtons[i].onClick.AddListener(() => { if (PlayerLoadout.Local != null) PlayerLoadout.Local.SetSpecial(type); Refresh(); });
        }
        closeButton.onClick.AddListener(() => SetOpen(false));
        screen.SetActive(false);
    }

    void OnDestroy() { input?.Disable(); input?.Dispose(); }

    void Update()
    {
        if (!open) return;
        NetGameManager gm = NetGameManager.Instance;
        if (PlayerLoadout.Local == null || gm != null && gm.InMatch) { SetOpen(false); return; } // out of games only
        if (input.GameControl.Cancel.WasPressedThisFrame()) SetOpen(false);
    }

    public void SetOpen(bool value)
    {
        NetGameManager gm = NetGameManager.Instance;
        if (value && (open || InputGate.Blocked || PlayerLoadout.Local == null || gm != null && gm.InMatch)) return;
        if (!value && !open) return;
        open = value;
        screen.SetActive(value);
        InputGate.Blocked = value;
        if (!value) { GameCursor.Close(this); return; }
        PlayerLoadout loadout = PlayerLoadout.Local;
        Button first = loadout != null && loadout.WeaponIndex < weaponButtons.Length ? weaponButtons[loadout.WeaponIndex] : null;
        GameCursor.Open(this, GameCursor.Use.Menu, first != null ? first.gameObject : null);
        Refresh();
    }

    void Refresh()
    {
        PlayerLoadout loadout = PlayerLoadout.Local;
        if (loadout == null) return;
        for (int i = 0; i < weaponButtons.Length; i++)
        {
            bool exists = i < loadout.Weapons.Count;
            weaponButtons[i].gameObject.SetActive(exists);
            if (!exists) continue;
            weaponButtons[i].GetComponentInChildren<TMP_Text>().text = loadout.Weapons[i].DisplayName;
            Select(weaponButtons[i], i == loadout.WeaponIndex);
        }
        for (int i = 0; i < subButtons.Length; i++) Select(subButtons[i], (SubType)i == loadout.Sub);
        for (int i = 0; i < specialButtons.Length; i++) Select(specialButtons[i], (SpecialType)i == loadout.Special);

        weaponInfo.text = loadout.CurrentWeapon != null ? loadout.CurrentWeapon.Description : "";
        subInfo.text = $"{SubDescription(loadout)}\nCosts {loadout.SubInkCost * 100f:0}% ink.";
        specialInfo.text = SpecialDescription(loadout);
    }

    // From the sub prefabs, so it follows their tuning.
    static string SubDescription(PlayerLoadout loadout)
    {
        if (loadout.Sub == SubType.Beacon && loadout.BeaconPrefab != null)
        {
            Beacon b = loadout.BeaconPrefab;
            return $"Placed in front of you. Your team can Super Jump to it. {b.MaxHealth:0} HP, lasts {b.Lifetime:0}s, breaks when landed on.";
        }
        if (loadout.Sub == SubType.Sprinkler && loadout.SprinklerPrefab != null)
        {
            Sprinkler s = loadout.SprinklerPrefab;
            return $"Thrown. Sticks where it lands and spins, spraying ink both ways, winding down over {s.Lifetime:0}s. {s.MaxHealth:0} HP.";
        }
        if (loadout.Sub == SubType.CurlingBomb && loadout.CurlingBombPrefab != null)
        {
            CurlingBomb c = loadout.CurlingBombPrefab;
            return $"Slides along the ground inking a stripe, bouncing off walls; {c.PassDamage:0} damage to anyone it passes. Explodes after {c.Fuse:0}s for {c.BlastDamage:0} damage. Hold the button to shorten the fuse.";
        }
        return "";
    }

    static string SpecialDescription(PlayerLoadout loadout)
    {
        if (loadout.Special == SpecialType.InkStrike && loadout.InkStrikePrefab != null)
        {
            InkStrike s = loadout.InkStrikePrefab;
            return $"Pick a spot on the map: {s.Delay:0.#}s later a column of ink {s.Radius * 2f:0}m wide covers it and splats everyone inside. Refills ink.";
        }
        return $"{loadout.ShieldDuration:0}s immune to damage. Refills ink.";
    }

    void Select(Button b, bool selected)
    {
        b.GetComponent<Image>().color = selected ? selectedColour : normalColour;
        b.GetComponentInChildren<TMP_Text>().color = selected ? new Color(0.1f, 0.1f, 0.12f) : Color.white;
    }
}

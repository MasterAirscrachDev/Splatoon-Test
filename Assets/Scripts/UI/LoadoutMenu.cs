using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

// Pick a main weapon, sub and special, any time out of a match: L / Start toggles it, as does walking
// up to the lobby's loadout kiosk; it closes itself when a match starts. Choices apply at once and are
// remembered (PlayerLoadout). Each choice is a tile with its icon and name ("Icon" and the text under
// it); each weapon's tile also has its power / range / speed as small bars ("Power/Fill" etc.), and
// the picked weapon's card shows them large, with its name and what it does.
public class LoadoutMenu : MonoBehaviour
{
    [SerializeField] GameObject screen;
    [SerializeField] Button[] weaponButtons; // in PlayerLoadout.Weapons order
    [SerializeField] Button[] subButtons;    // in SubType order
    [SerializeField] Button[] specialButtons; // in SpecialType order
    [SerializeField] TMP_Text weaponInfo, subInfo, specialInfo;
    [SerializeField] TMP_Text weaponName;
    [SerializeField] RectTransform powerBar, rangeBar, speedBar; // the bars' fills (their width is the rating)
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

    bool warnedButtons;

    void Refresh()
    {
        PlayerLoadout loadout = PlayerLoadout.Local;
        if (loadout == null) return;
        if (loadout.Weapons.Count > weaponButtons.Length && !warnedButtons)
        {
            warnedButtons = true;
            Debug.LogWarning($"[LoadoutMenu] {loadout.Weapons.Count} weapons but {weaponButtons.Length} buttons: rebuild with Tools > UI > Build Loadout Menu Prefab");
        }
        for (int i = 0; i < weaponButtons.Length; i++)
        {
            bool exists = i < loadout.Weapons.Count;
            weaponButtons[i].gameObject.SetActive(exists);
            if (!exists) continue;
            weaponButtons[i].GetComponentInChildren<TMP_Text>().text = loadout.Weapons[i].DisplayName;
            SetIcon(weaponButtons[i], loadout.Weapons[i].Icon);
            Weapon w = loadout.Weapons[i];
            SetBar(weaponButtons[i].transform.Find("Power/Fill") as RectTransform, w.PowerRating);
            SetBar(weaponButtons[i].transform.Find("Range/Fill") as RectTransform, w.RangeRating);
            SetBar(weaponButtons[i].transform.Find("Speed/Fill") as RectTransform, w.SpeedRating);
            Select(weaponButtons[i], i == loadout.WeaponIndex);
        }
        for (int i = 0; i < subButtons.Length; i++)
        {
            SetIcon(subButtons[i], loadout.SubIconOf((SubType)i));
            Select(subButtons[i], (SubType)i == loadout.Sub);
        }
        for (int i = 0; i < specialButtons.Length; i++)
        {
            SetIcon(specialButtons[i], loadout.SpecialIconOf((SpecialType)i));
            Select(specialButtons[i], (SpecialType)i == loadout.Special);
        }

        Weapon current = loadout.CurrentWeapon;
        weaponInfo.text = current != null ? current.Description : "";
        if (weaponName != null) weaponName.text = current != null ? current.DisplayName : "";
        SetBar(powerBar, current != null ? current.PowerRating : 0f);
        SetBar(rangeBar, current != null ? current.RangeRating : 0f);
        SetBar(speedBar, current != null ? current.SpeedRating : 0f);
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
            return $"Slides along the ground inking a stripe, bouncing off walls; {c.PassDamage:0} damage to anyone it passes. Explodes after {c.Fuse:0}s for {c.BlastDamage:0} damage. Hold the button to cook it: the fuse runs {c.CookRate:0.#}x as fast.";
        }
        if (loadout.BombPrefab(loadout.Sub) is ThrownBomb t && t != null)
        {
            string how = t.Contact == ThrownBomb.ContactMode.Stick ? $"Thrown. Sticks to whatever it hits, walls too, and goes off {t.Fuse:0.#}s later"
                       : t.Contact == ThrownBomb.ContactMode.Burst ? "Thrown. Goes off the moment it hits anything, or anyone"
                       : $"Thrown. Bounces and rolls to a stop, going off {t.Fuse:0.#}s after it first lands";
            return $"{how}: up to {t.Blast.coreDamage:0} damage close in, {t.Blast.edgeDamage:0} at the edge of its {t.Blast.radius:0.#}m blast.";
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

    public float BarFill(int stat) => (stat == 0 ? powerBar : stat == 1 ? rangeBar : speedBar) is RectTransform r && r != null ? r.anchorMax.x : -1f; // tests
    public string WeaponName => weaponName != null ? weaponName.text : "";

    static void SetIcon(Button b, Sprite sprite)
    {
        Transform t = b.transform.Find("Icon");
        if (t == null || !t.TryGetComponent(out Image icon)) return;
        icon.sprite = sprite;
        icon.enabled = sprite != null; // the name alone without one
    }

    static void SetBar(RectTransform fill, float rating)
    {
        if (fill == null) return;
        fill.anchorMax = new Vector2(Mathf.Clamp01(rating), 1f);
    }

    void Select(Button b, bool selected)
    {
        b.GetComponent<Image>().color = selected ? selectedColour : normalColour;
        b.GetComponentInChildren<TMP_Text>().color = selected ? new Color(0.1f, 0.1f, 0.12f) : Color.white;
    }
}

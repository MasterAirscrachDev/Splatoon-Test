using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

// Placeholder (until the hub): pick a main weapon and sub between matches. L / Start toggles it;
// it closes itself when a match starts. Choices apply at once and are remembered (PlayerLoadout).
public class LoadoutMenu : MonoBehaviour
{
    [SerializeField] GameObject screen;
    [SerializeField] Button[] weaponButtons; // in PlayerLoadout.Weapons order
    [SerializeField] Button[] subButtons;    // in SubType order
    [SerializeField] TMP_Text weaponInfo, subInfo;
    [SerializeField] Button closeButton;
    [SerializeField] Color normalColour = new Color(0.2f, 0.2f, 0.26f, 1f);
    [SerializeField] Color selectedColour = new Color(1f, 0.82f, 0.25f, 1f);

    ControlLayer input;
    bool open;

    public bool IsOpen => open;
    public Button[] WeaponButtons => weaponButtons;
    public Button[] SubButtons => subButtons;

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
        closeButton.onClick.AddListener(() => SetOpen(false));
        screen.SetActive(false);
    }

    void OnDestroy() => input?.Dispose();

    void Update()
    {
        if (!open) return;
        NetGameManager gm = NetGameManager.Instance;
        if (PlayerLoadout.Local == null || gm != null && gm.InMatch) { SetOpen(false); return; } // out of games only
        if (Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame) SetOpen(false);
    }

    public void SetOpen(bool value)
    {
        NetGameManager gm = NetGameManager.Instance;
        if (value && (open || InputGate.Blocked || PlayerLoadout.Local == null || gm != null && gm.InMatch)) return;
        if (!value && !open) return;
        open = value;
        screen.SetActive(value);
        InputGate.Blocked = value;
        Cursor.lockState = value ? CursorLockMode.None : CursorLockMode.Locked;
        Cursor.visible = value;
        if (!value) return;
        HostMenu.EnsureEventSystem();
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

        weaponInfo.text = loadout.CurrentWeapon != null ? loadout.CurrentWeapon.Description : "";
        subInfo.text = $"{SubDescription(loadout)}\nCosts {loadout.SubInkCost * 100f:0}% ink.";
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
        return "";
    }

    void Select(Button b, bool selected)
    {
        b.GetComponent<Image>().color = selected ? selectedColour : normalColour;
        b.GetComponentInChildren<TMP_Text>().color = selected ? new Color(0.1f, 0.1f, 0.12f) : Color.white;
    }
}

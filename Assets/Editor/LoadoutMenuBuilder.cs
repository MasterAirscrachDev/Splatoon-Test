using System.IO;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using static MatchHUDBuilder;

// The loadout menu (see LoadoutMenu), a panel in Assets/Prefabs/UI/GameUI.prefab: the loadout picker
// (any time out of a match; also the lobby's loadout kiosk), in MatchHUDBuilder's look. The main
// weapons are a grid of icon tiles with the picked one's card beside it (name, power / range / speed
// bars, description); the subs and specials are rows of tiles under the card. Tools/UI/Rework
// Loadout Menu rebuilds just the panel in place.
public static class LoadoutMenuBuilder
{
    const string PrefabPath = "Assets/Prefabs/UI/GameUI.prefab";
    const int WeaponSlots = 15, GridColumns = 5;          // tiles past the loadout's weapons are hidden
    const float PanelWidth = 1480, PanelHeight = 900;
    const float WeaponTile = 140, WeaponStep = 142, WeaponRowStep = 182, SmallTile = 112, SmallStep = 124, LabelHeight = 26;
    const float WeaponTileScale = 0.93f;                  // five across still clears the card
    const float LeftX = -370, RightX = 370;               // the columns' centres
    static readonly Color PowerColour = new Color(1f, 0.3f, 0.72f), RangeColour = new Color(0.35f, 0.9f, 0.4f), SpeedColour = new Color(0.25f, 0.85f, 1f);

    // Replaces just the panel (its screen and the menu's wiring stay).
    [MenuItem("Tools/UI/Rework Loadout Menu (Icons And Stats)")]
    public static void Rework()
    {
        LoadShared();
        GameObject root = PrefabUtility.LoadPrefabContents(PrefabPath);
        try
        {
            LoadoutMenu menu = root.GetComponentInChildren<LoadoutMenu>(true);
            var screen = (RectTransform)((GameObject)new SerializedObject(menu).FindProperty("screen").objectReferenceValue).transform;
            Transform old = screen.Find("Panel");
            if (old != null) Object.DestroyImmediate(old.gameObject);
            BuildPanel(screen, menu);
            PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }
        Debug.Log($"[LoadoutMenu] Reworked {PrefabPath}: icon tiles and weapon stats");
    }

    static void BuildPanel(RectTransform screen, LoadoutMenu menu)
    {
        RectTransform panel = Panel("Panel", screen, new Vector2(PanelWidth, PanelHeight));
        Title(panel, "LOADOUT");
        TextMeshProUGUI note = Text("Note", panel, "Any time out of a match: L / Start, or the lobby's loadout kiosk  ·  changes apply now", 18, new Color(1f, 1f, 1f, 0.5f), TextAlignmentOptions.Center, false, useOutline: false);
        Place(note.rectTransform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0, -64), new Vector2(900, 26));

        // Main weapons: a grid of tiles on the left.
        Header(panel, "MAIN WEAPON", LeftX, -100, 640);
        var weapons = new Button[WeaponSlots];
        for (int i = 0; i < WeaponSlots; i++)
        {
            weapons[i] = Tile($"Weapon{i}", panel, "Weapon", Vector2.zero, WeaponTile, bars: true);
            PlaceWeaponTile(weapons[i], i);
        }

        // The picked weapon's card on the right: name, three bars, what it does.
        TextMeshProUGUI weaponName = Text("WeaponName", panel, "Weapon", 36, Color.white, TextAlignmentOptions.Left, true);
        Place(weaponName.rectTransform, new Vector2(0.5f, 1f), new Vector2(0f, 1f), new Vector2(RightX - 330, -100), new Vector2(660, 46));
        RectTransform power = StatBar(panel, "POWER", -166, PowerColour);
        RectTransform range = StatBar(panel, "RANGE", -202, RangeColour);
        RectTransform speed = StatBar(panel, "SPEED", -238, SpeedColour);
        TextMeshProUGUI weaponInfo = Text("WeaponInfo", panel, "", 18, new Color(1f, 1f, 1f, 0.75f), TextAlignmentOptions.TopLeft, false, useOutline: false);
        Place(weaponInfo.rectTransform, new Vector2(0.5f, 1f), new Vector2(0f, 1f), new Vector2(RightX - 330, -276), new Vector2(660, 110));
        weaponInfo.textWrappingMode = TextWrappingModes.Normal;

        // Subs and specials: rows of smaller tiles under the card.
        float subX = RightX - 170, specialX = RightX + 200;
        Button[] subs = TileRow(panel, "SUB", subX, SubNames, 340, out TextMeshProUGUI subInfo);
        LayoutSubTiles(subs, subInfo);
        Button[] specials = TileRow(panel, "SPECIAL", specialX, new[] { "Bubble Shield", "InkStrike" }, 300, out TextMeshProUGUI specialInfo);

        Button close = MenuButton("Close", panel, "Close", new Vector2(0, -(PanelHeight - 80)), new Vector2(180, 46), out _);
        TextMeshProUGUI hint = Text("Hint", panel, "L / Esc to close", 16, new Color(1f, 1f, 1f, 0.5f), TextAlignmentOptions.Center, false, useOutline: false);
        Prompt(hint, "{GameControl/Loadout} / {GameControl/Cancel} to close");
        Place(hint.rectTransform, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0, 10), new Vector2(300, 24));

        var so = new SerializedObject(menu);
        Assign(so.FindProperty("weaponButtons"), weapons);
        Assign(so.FindProperty("subButtons"), subs);
        Assign(so.FindProperty("specialButtons"), specials);
        so.FindProperty("weaponInfo").objectReferenceValue = weaponInfo;
        so.FindProperty("subInfo").objectReferenceValue = subInfo;
        so.FindProperty("specialInfo").objectReferenceValue = specialInfo;
        so.FindProperty("weaponName").objectReferenceValue = weaponName;
        so.FindProperty("powerBar").objectReferenceValue = power;
        so.FindProperty("rangeBar").objectReferenceValue = range;
        so.FindProperty("speedBar").objectReferenceValue = speed;
        so.FindProperty("closeButton").objectReferenceValue = close;
        so.ApplyModifiedPropertiesWithoutUndo();
    }

    // The subs' tiles in SubType order (labels; the icons come from the prefabs).
    static readonly string[] SubNames = { "Beacon", "Ink Sprinkler", "Curling Bomb", "Suction Bomb", "Impact Bomb", "Ink Bomb" };
    const int SubColumns = 3;
    const float SubTileScale = 0.8f, SubTop = -474, SubX = RightX - 170;

    // The subs as a grid SubColumns across (scaled down to fit two rows), their description under it.
    static void LayoutSubTiles(Button[] subs, TextMeshProUGUI info)
    {
        float step = SmallStep * SubTileScale, rowStep = (SmallTile + LabelHeight) * SubTileScale + 10f;
        for (int i = 0; i < subs.Length; i++)
        {
            int col = i % SubColumns, row = i / SubColumns;
            var rt = (RectTransform)subs[i].transform;
            rt.anchoredPosition = new Vector2(SubX + (col - (SubColumns - 1) * 0.5f) * step, SubTop - row * rowStep);
            rt.localScale = Vector3.one * SubTileScale;
        }
        int rows = (subs.Length + SubColumns - 1) / SubColumns;
        if (info != null)
        {
            info.rectTransform.anchoredPosition = new Vector2(SubX, SubTop - (rows - 1) * rowStep - (SmallTile + LabelHeight) * SubTileScale - 14f);
            info.rectTransform.sizeDelta = new Vector2(340, 110);
        }
    }

    // Migration: grows the existing panel's sub tiles to one per sub (copies of its last tile, relabelled)
    // and lays them out as a grid.
    [MenuItem("Tools/UI/Fit Loadout Menu Sub Slots")]
    public static void FitSubSlotsMenu() => Debug.Log(FitSubSlots());

    public static string FitSubSlots()
    {
        GameObject root = PrefabUtility.LoadPrefabContents(PrefabPath);
        try
        {
            LoadoutMenu menu = root.GetComponentInChildren<LoadoutMenu>(true);
            var so = new SerializedObject(menu);
            SerializedProperty array = so.FindProperty("subButtons");
            var tiles = new System.Collections.Generic.List<Button>();
            for (int i = 0; i < array.arraySize; i++) tiles.Add((Button)array.GetArrayElementAtIndex(i).objectReferenceValue);
            int had = tiles.Count;
            if (had == 0) return "[LoadoutMenu] no sub tiles to copy";
            Button last = tiles[had - 1];
            while (tiles.Count < SubNames.Length)
            {
                var copy = Object.Instantiate(last.gameObject, last.transform.parent);
                copy.name = SubNames[tiles.Count];
                copy.transform.SetSiblingIndex(tiles[tiles.Count - 1].transform.GetSiblingIndex() + 1);
                TMP_Text label = copy.GetComponentInChildren<TMP_Text>(true);
                if (label != null) label.text = SubNames[tiles.Count];
                tiles.Add(copy.GetComponent<Button>());
            }
            LayoutSubTiles(tiles.ToArray(), (TextMeshProUGUI)so.FindProperty("subInfo").objectReferenceValue);
            Assign(array, tiles.ToArray());
            so.ApplyModifiedPropertiesWithoutUndo();
            PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
            return $"[LoadoutMenu] sub tiles {had} -> {tiles.Count}, {SubColumns} across";
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }
    }

    // Weapon tile i's spot in the grid (scaled to fit GridColumns across).
    static void PlaceWeaponTile(Button tile, int i)
    {
        int col = i % GridColumns, row = i / GridColumns;
        var rt = (RectTransform)tile.transform;
        rt.anchoredPosition = new Vector2(LeftX + (col - (GridColumns - 1) * 0.5f) * WeaponStep, -142 - row * WeaponRowStep);
        rt.localScale = Vector3.one * WeaponTileScale;
    }

    // Migration: grows the existing panel's weapon grid to WeaponSlots (copies of its last tile, so any
    // hand changes carry over) and lays it out GridColumns across.
    [MenuItem("Tools/UI/Fit Loadout Menu Weapon Slots")]
    public static void FitWeaponSlotsMenu() => Debug.Log(FitWeaponSlots());

    public static string FitWeaponSlots()
    {
        GameObject root = PrefabUtility.LoadPrefabContents(PrefabPath);
        try
        {
            LoadoutMenu menu = root.GetComponentInChildren<LoadoutMenu>(true);
            var so = new SerializedObject(menu);
            SerializedProperty array = so.FindProperty("weaponButtons");
            var tiles = new System.Collections.Generic.List<Button>();
            for (int i = 0; i < array.arraySize; i++) tiles.Add((Button)array.GetArrayElementAtIndex(i).objectReferenceValue);
            int had = tiles.Count;
            if (had == 0) return "[LoadoutMenu] no weapon tiles to copy";
            Button last = tiles[had - 1];
            while (tiles.Count < WeaponSlots)
            {
                var copy = Object.Instantiate(last.gameObject, last.transform.parent);
                copy.name = $"Weapon{tiles.Count}";
                copy.transform.SetSiblingIndex(tiles[tiles.Count - 1].transform.GetSiblingIndex() + 1);
                tiles.Add(copy.GetComponent<Button>());
            }
            for (int i = 0; i < tiles.Count; i++) PlaceWeaponTile(tiles[i], i);
            Assign(array, tiles.ToArray());
            so.ApplyModifiedPropertiesWithoutUndo();
            PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
            return $"[LoadoutMenu] weapon tiles {had} -> {tiles.Count}, {GridColumns} across";
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }
    }

    static void Header(RectTransform panel, string text, float x, float y, float width)
    {
        TextMeshProUGUI title = Text(text, panel, text, 24, Color.white, TextAlignmentOptions.Center, true);
        Place(title.rectTransform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(x, y), new Vector2(width, 34));
    }

    // A square button: the icon filling its top, the name under it (and, for weapons, three small
    // bars between them: power, range, speed).
    static Button Tile(string name, RectTransform parent, string label, Vector2 pos, float size, bool bars = false)
    {
        Button button = MenuButton(name, parent, label, pos, new Vector2(size, size + LabelHeight), out TextMeshProUGUI text);
        text.fontSize = size >= WeaponTile ? 17 : 15;
        text.enableAutoSizing = true;                        // long names shrink to fit
        text.fontSizeMin = 11;
        text.fontSizeMax = text.fontSize;
        text.overflowMode = TextOverflowModes.Overflow;
        Place(text.rectTransform, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0, 4), new Vector2(size - 8, LabelHeight - 4));
        float iconSize = bars ? size - 34 : size - 14;
        RectTransform iconRt = Rect("Icon", button.transform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0, -6), new Vector2(iconSize, iconSize));
        if (bars)
        {
            (string, Color)[] stats = { ("Power", PowerColour), ("Range", RangeColour), ("Speed", SpeedColour) };
            for (int i = 0; i < stats.Length; i++)
            {
                RectTransform back = Rect(stats[i].Item1, button.transform, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0, LabelHeight + 4 + (2 - i) * 8), new Vector2(size - 24, 5));
                Img(back.gameObject, Rounded, new Color(1f, 1f, 1f, 0.12f), sliced: true);
                RectTransform fill = Rect("Fill", back, Vector2.zero, Vector2.zero, Vector2.zero, Vector2.zero);
                fill.anchorMin = Vector2.zero;
                fill.anchorMax = new Vector2(0.5f, 1f);
                fill.offsetMin = fill.offsetMax = Vector2.zero;
                Img(fill.gameObject, Rounded, stats[i].Item2, sliced: true);
            }
        }
        Image icon = Img(iconRt.gameObject, null, Color.white, sliced: false);
        icon.preserveAspect = true;
        icon.enabled = false; // until there's an icon to show
        return button;
    }

    static Button[] TileRow(RectTransform panel, string header, float x, string[] options, float width, out TextMeshProUGUI info)
    {
        Header(panel, header, x, -432, width);
        var buttons = new Button[options.Length];
        for (int i = 0; i < options.Length; i++)
            buttons[i] = Tile(options[i], panel, options[i], new Vector2(x + (i - (options.Length - 1) * 0.5f) * SmallStep, -474), SmallTile);
        info = Text(header + "Info", panel, "", 17, new Color(1f, 1f, 1f, 0.75f), TextAlignmentOptions.Top, false, useOutline: false);
        Place(info.rectTransform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(x, -474 - SmallTile - LabelHeight - 14), new Vector2(width, 170));
        info.textWrappingMode = TextWrappingModes.Normal;
        return buttons;
    }

    // "LABEL ▬▬▬▬▬▬" on the card: returns the fill (its width is the rating).
    static RectTransform StatBar(RectTransform panel, string label, float y, Color colour)
    {
        TextMeshProUGUI text = Text(label + "Label", panel, label, 18, new Color(1f, 1f, 1f, 0.8f), TextAlignmentOptions.Left, true, useOutline: false);
        Place(text.rectTransform, new Vector2(0.5f, 1f), new Vector2(0f, 1f), new Vector2(RightX - 330, y), new Vector2(110, 26));
        RectTransform back = Rect(label + "Bar", panel, new Vector2(0.5f, 1f), new Vector2(0f, 1f), new Vector2(RightX - 210, y - 5), new Vector2(520, 16));
        Img(back.gameObject, Rounded, new Color(1f, 1f, 1f, 0.12f), sliced: true);
        RectTransform fill = Rect("Fill", back, Vector2.zero, Vector2.zero, Vector2.zero, Vector2.zero);
        fill.anchorMin = Vector2.zero;
        fill.anchorMax = new Vector2(0.5f, 1f);
        fill.offsetMin = fill.offsetMax = Vector2.zero;
        Img(fill.gameObject, Rounded, colour, sliced: true);
        return fill;
    }

    static void Assign(SerializedProperty array, Button[] buttons)
    {
        array.arraySize = buttons.Length;
        for (int i = 0; i < buttons.Length; i++) array.GetArrayElementAtIndex(i).objectReferenceValue = buttons[i];
    }
}

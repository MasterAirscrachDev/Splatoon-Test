using System.IO;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using static MatchHUDBuilder;

// Generates Assets/Prefabs/UI/LoadoutMenu.prefab (see LoadoutMenu): the placeholder between-match
// loadout picker, on its own canvas (any time out of a match; also the lobby's loadout kiosk). Reuses MatchHUDBuilder's look. The main weapons are a grid of
// icon tiles with the picked one's card beside it (name, power / range / speed bars, description); the
// subs and specials are rows of tiles under the card. Tools/UI/Rework Loadout Menu rebuilds just the
// panel on the existing prefab.
public static class LoadoutMenuBuilder
{
    const string PrefabPath = "Assets/Prefabs/UI/LoadoutMenu.prefab";
    const int WeaponSlots = 12, GridColumns = 4;          // tiles past the loadout's weapons are hidden
    const float PanelWidth = 1480, PanelHeight = 900;
    const float WeaponTile = 140, WeaponStep = 156, WeaponRowStep = 182, SmallTile = 112, SmallStep = 124, LabelHeight = 26;
    const float LeftX = -370, RightX = 370;               // the columns' centres
    static readonly Color PowerColour = new Color(1f, 0.3f, 0.72f), RangeColour = new Color(0.35f, 0.9f, 0.4f), SpeedColour = new Color(0.25f, 0.85f, 1f);

    [MenuItem("Tools/UI/Build Loadout Menu Prefab")]
    public static void Build()
    {
        LoadShared();
        Scene preview = EditorSceneManager.NewPreviewScene();
        var root = new GameObject("LoadoutMenu", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
        SceneManager.MoveGameObjectToScene(root, preview);
        Canvas canvas = root.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 25; // over the HUD, under the lobby menu
        CanvasScaler scaler = root.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080);
        scaler.matchWidthOrHeight = 1f;

        Vector2 centre = new Vector2(0.5f, 0.5f);
        RectTransform screen = Rect("Screen", root.transform, Vector2.zero, centre, Vector2.zero, Vector2.zero);
        Stretch(screen);
        RectTransform backdrop = Rect("Backdrop", screen, Vector2.zero, centre, Vector2.zero, Vector2.zero);
        Stretch(backdrop);
        Img(backdrop.gameObject, null, new Color(0.02f, 0.02f, 0.05f, 0.6f), sliced: false).raycastTarget = true;

        LoadoutMenu menu = root.AddComponent<LoadoutMenu>();
        var so = new SerializedObject(menu);
        so.FindProperty("screen").objectReferenceValue = screen.gameObject;
        so.ApplyModifiedPropertiesWithoutUndo();
        BuildPanel(screen, menu);
        screen.gameObject.SetActive(false);

        Directory.CreateDirectory(Path.GetDirectoryName(PrefabPath));
        PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
        EditorSceneManager.ClosePreviewScene(preview);
        Debug.Log($"[LoadoutMenu] Built {PrefabPath}");
    }

    // Replaces just the panel on the existing prefab (its canvas, screen and the scenes' instances stay).
    [MenuItem("Tools/UI/Rework Loadout Menu (Icons And Stats)")]
    public static void Rework()
    {
        LoadShared();
        GameObject root = PrefabUtility.LoadPrefabContents(PrefabPath);
        try
        {
            LoadoutMenu menu = root.GetComponent<LoadoutMenu>();
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
            int col = i % GridColumns, row = i / GridColumns;
            var pos = new Vector2(LeftX + (col - (GridColumns - 1) * 0.5f) * WeaponStep, -142 - row * WeaponRowStep);
            weapons[i] = Tile($"Weapon{i}", panel, "Weapon", pos, WeaponTile, bars: true);
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
        Button[] subs = TileRow(panel, "SUB", subX, new[] { "Beacon", "Ink Sprinkler", "Curling Bomb" }, 340, out TextMeshProUGUI subInfo);
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

using System.IO;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using static MatchHUDBuilder;

// Generates Assets/Prefabs/UI/LoadoutMenu.prefab (see LoadoutMenu): the placeholder between-match
// loadout picker, on its own canvas. Reuses MatchHUDBuilder's look.
public static class LoadoutMenuBuilder
{
    const string PrefabPath = "Assets/Prefabs/UI/LoadoutMenu.prefab";
    const float ColumnWidth = 360, InfoHeight = 120;
    const int Rows = 4;                               // buttons in the longest column
    const float RowStep = 68, InfoTop = -156 - (Rows - 1) * RowStep - 86; // descriptions under the last row
    const float CloseY = InfoTop - 160, PanelHeight = -CloseY + 90;

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

        RectTransform panel = Panel("Panel", screen, new Vector2(3 * ColumnWidth + 160, PanelHeight));
        Title(panel, "LOADOUT");
        TextMeshProUGUI note = Text("Note", panel, "Placeholder until the hub exists  Â·  changes apply now", 18, new Color(1f, 1f, 1f, 0.5f), TextAlignmentOptions.Center, false, useOutline: false);
        Place(note.rectTransform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0, -64), new Vector2(900, 26));

        float[] x = { -(ColumnWidth + 40), 0, ColumnWidth + 40 };
        Button[] weapons = Column(panel, "MAIN WEAPON", x[0], new[] { "Airspray SE", "Inkshot", "Blaster", "Inkroller" }, out TextMeshProUGUI weaponInfo);
        Button[] subs = Column(panel, "SUB", x[1], new[] { "Beacon", "Ink Sprinkler", "Curling Bomb" }, out TextMeshProUGUI subInfo);
        Button[] specials = Column(panel, "SPECIAL", x[2], new[] { "Bubble Shield", "InkStrike" }, out TextMeshProUGUI specialInfo);

        Button close = MenuButton("Close", panel, "Close", new Vector2(0, CloseY), new Vector2(180, 46), out _);
        TextMeshProUGUI hint = Text("Hint", panel, "L / Esc to close", 16, new Color(1f, 1f, 1f, 0.5f), TextAlignmentOptions.Center, false, useOutline: false);
        Prompt(hint, "{GameControl/Loadout} / {GameControl/Cancel} to close");
        Place(hint.rectTransform, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0, 10), new Vector2(300, 24));

        LoadoutMenu menu = root.AddComponent<LoadoutMenu>();
        var so = new SerializedObject(menu);
        so.FindProperty("screen").objectReferenceValue = screen.gameObject;
        Assign(so.FindProperty("weaponButtons"), weapons);
        Assign(so.FindProperty("subButtons"), subs);
        Assign(so.FindProperty("specialButtons"), specials);
        so.FindProperty("specialInfo").objectReferenceValue = specialInfo;
        so.FindProperty("weaponInfo").objectReferenceValue = weaponInfo;
        so.FindProperty("subInfo").objectReferenceValue = subInfo;
        so.FindProperty("closeButton").objectReferenceValue = close;
        so.ApplyModifiedPropertiesWithoutUndo();
        screen.gameObject.SetActive(false);

        Directory.CreateDirectory(Path.GetDirectoryName(PrefabPath));
        PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
        EditorSceneManager.ClosePreviewScene(preview);
        Debug.Log($"[LoadoutMenu] Built {PrefabPath}");
    }

    // Header, a button per option, and a description underneath.
    static Button[] Column(RectTransform panel, string header, float x, string[] options, out TextMeshProUGUI info)
    {
        TextMeshProUGUI title = Text(header, panel, header, 24, Color.white, TextAlignmentOptions.Center, true);
        Place(title.rectTransform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(x, -110), new Vector2(ColumnWidth, 34));
        var buttons = new Button[options.Length];
        for (int i = 0; i < options.Length; i++)
            buttons[i] = MenuButton(options[i], panel, options[i], new Vector2(x, -156 - i * RowStep), new Vector2(ColumnWidth - 20, 58), out _);
        info = Text(header + "Info", panel, "", 18, new Color(1f, 1f, 1f, 0.75f), TextAlignmentOptions.Top, false, useOutline: false);
        Place(info.rectTransform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(x, InfoTop), new Vector2(ColumnWidth - 10, InfoHeight));
        info.textWrappingMode = TextWrappingModes.Normal;
        return buttons;
    }

    static void Assign(SerializedProperty array, Button[] buttons)
    {
        array.arraySize = buttons.Length;
        for (int i = 0; i < buttons.Length; i++) array.GetArrayElementAtIndex(i).objectReferenceValue = buttons[i];
    }
}

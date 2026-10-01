using System.IO;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using static MatchHUDBuilder;

// Generates Assets/Prefabs/UI/LobbyMenu.prefab (see LobbyMenu): the placeholder host/join screen,
// on its own canvas, separate from the match HUD. Reuses MatchHUDBuilder's look.
public static class LobbyMenuBuilder
{
    const string PrefabPath = "Assets/Prefabs/UI/LobbyMenu.prefab";
    const float PanelWidth = 1040, PanelHeight = 660, ListWidth = 580, ListHeight = 400, RowHeight = 64;

    [MenuItem("Tools/UI/Build Lobby Menu Prefab")]
    public static void Build()
    {
        LoadShared();
        Scene preview = EditorSceneManager.NewPreviewScene();
        var root = new GameObject("LobbyMenu", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
        SceneManager.MoveGameObjectToScene(root, preview);
        Canvas canvas = root.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 30; // above the HUD and its menus
        CanvasScaler scaler = root.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080);
        scaler.matchWidthOrHeight = 1f;

        Vector2 centre = new Vector2(0.5f, 0.5f);
        RectTransform screen = Rect("Screen", root.transform, Vector2.zero, centre, Vector2.zero, Vector2.zero);
        Stretch(screen);
        RectTransform backdrop = Rect("Backdrop", screen, Vector2.zero, centre, Vector2.zero, Vector2.zero);
        Stretch(backdrop);
        Img(backdrop.gameObject, null, new Color(0.02f, 0.02f, 0.05f, 0.8f), sliced: false).raycastTarget = true;

        RectTransform panel = Panel("Panel", screen, new Vector2(PanelWidth, PanelHeight));
        Title(panel, "PLAY");
        TextMeshProUGUI note = Text("Note", panel, "Placeholder until the hub exists", 18, new Color(1f, 1f, 1f, 0.5f), TextAlignmentOptions.Center, false, useOutline: false);
        Place(note.rectTransform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0, -64), new Vector2(600, 26));

        // Left: host.
        float leftX = -PanelWidth / 2f + 40 + 160;
        Button host = MenuButton("Host", panel, "Host lobby", new Vector2(leftX, -150), new Vector2(320, 96), out TextMeshProUGUI hostLabel);
        hostLabel.fontSize = 32;
        TextMeshProUGUI hostNote = Text("HostNote", panel, "Public: anyone running the game can see and join it", 17,
                                        new Color(1f, 1f, 1f, 0.55f), TextAlignmentOptions.Center, false, useOutline: false);
        Place(hostNote.rectTransform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(leftX, -256), new Vector2(320, 50));
        hostNote.textWrappingMode = TextWrappingModes.Normal;

        // Right: lobby list.
        float listX = PanelWidth / 2f - 40 - ListWidth / 2f;
        TextMeshProUGUI header = Text("ListHeader", panel, "LOBBIES", 24, Color.white, TextAlignmentOptions.Left, true);
        Place(header.rectTransform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(listX, -112), new Vector2(ListWidth, 36));
        Button refresh = MenuButton("Refresh", panel, "Refresh", new Vector2(listX + ListWidth / 2f - 70, -106), new Vector2(140, 44), out TextMeshProUGUI refreshLabel);
        refreshLabel.fontSize = 20;

        RectTransform list = Rect("List", panel, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(listX, -160), new Vector2(ListWidth, ListHeight));
        Img(list.gameObject, Rounded, new Color(0f, 0f, 0f, 0.35f), sliced: true);
        RectTransform viewport = Rect("Viewport", list, Vector2.zero, centre, Vector2.zero, Vector2.zero);
        StretchInset(viewport, 8);
        viewport.gameObject.AddComponent<RectMask2D>();
        RectTransform content = Rect("Content", viewport, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), Vector2.zero, new Vector2(0, 0));
        content.anchorMin = new Vector2(0f, 1f);
        content.anchorMax = new Vector2(1f, 1f);
        content.sizeDelta = Vector2.zero;
        var layout = content.gameObject.AddComponent<VerticalLayoutGroup>();
        layout.spacing = 8;
        layout.childControlHeight = false;
        layout.childControlWidth = true;
        layout.childForceExpandHeight = false;
        layout.childForceExpandWidth = true;
        content.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        var scroll = list.gameObject.AddComponent<ScrollRect>();
        scroll.viewport = viewport;
        scroll.content = content;
        scroll.horizontal = false;
        scroll.movementType = ScrollRect.MovementType.Clamped;
        scroll.scrollSensitivity = 30;

        LobbyRow row = Row(content);
        TextMeshProUGUI empty = Text("Empty", list, "No lobbies yet: host one!", 22, new Color(1f, 1f, 1f, 0.45f), TextAlignmentOptions.Center, false, useOutline: false);
        Stretch(empty.rectTransform);

        TextMeshProUGUI status = Text("Status", panel, "Connecting to Steam…", 20, new Color(1f, 1f, 1f, 0.8f), TextAlignmentOptions.Center, false, useOutline: false);
        Place(status.rectTransform, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0, 30), new Vector2(PanelWidth - 80, 30));

        LobbyMenu menu = root.AddComponent<LobbyMenu>();
        var so = new SerializedObject(menu);
        so.FindProperty("screen").objectReferenceValue = screen.gameObject;
        so.FindProperty("hostButton").objectReferenceValue = host;
        so.FindProperty("refreshButton").objectReferenceValue = refresh;
        so.FindProperty("status").objectReferenceValue = status;
        so.FindProperty("rowTemplate").objectReferenceValue = row;
        so.FindProperty("emptyLabel").objectReferenceValue = empty.gameObject;
        so.FindProperty("autoHostInEditor").boolValue = true; // editor sessions host straight away
        so.ApplyModifiedPropertiesWithoutUndo();
        screen.gameObject.SetActive(false);

        Directory.CreateDirectory(Path.GetDirectoryName(PrefabPath));
        PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
        EditorSceneManager.ClosePreviewScene(preview);
        Debug.Log($"[LobbyMenu] Built {PrefabPath}");
    }

    // Host name, player count and a join button.
    static LobbyRow Row(RectTransform content)
    {
        RectTransform rt = Rect("Row", content, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), Vector2.zero, new Vector2(0, RowHeight));
        Img(rt.gameObject, Rounded, new Color(0.12f, 0.12f, 0.17f, 1f), sliced: true); // darker than the buttons
        TextMeshProUGUI name = Text("Host", rt, "Host", 24, Color.white, TextAlignmentOptions.Left, true, useOutline: false);
        Place(name.rectTransform, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(20, 0), new Vector2(300, 40));
        name.textWrappingMode = TextWrappingModes.NoWrap;
        name.overflowMode = TextOverflowModes.Ellipsis;
        TextMeshProUGUI players = Text("Players", rt, "1/8", 22, new Color(1f, 1f, 1f, 0.7f), TextAlignmentOptions.Right, false, useOutline: false);
        Place(players.rectTransform, new Vector2(1f, 0.5f), new Vector2(1f, 0.5f), new Vector2(-150, 0), new Vector2(80, 40));
        Button join = MenuButton("Join", rt, "Join", Vector2.zero, new Vector2(120, 46), out TextMeshProUGUI joinLabel);
        Place((RectTransform)join.transform, new Vector2(1f, 0.5f), new Vector2(1f, 0.5f), new Vector2(-10, 0), new Vector2(120, 46));
        joinLabel.fontSize = 22;

        LobbyRow row = rt.gameObject.AddComponent<LobbyRow>();
        row.hostName = name;
        row.players = players;
        row.join = join;
        return row;
    }
}

using System.IO;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

// Generates Assets/Prefabs/UI/MatchHUD.prefab: the top-of-screen match HUD (see MatchHUD) plus
// the host menu (see HostMenu) and the map (see MapScreen). Re-run after layout changes, or edit
// the prefab directly.
public static class MatchHUDBuilder
{
    const string PrefabPath = "Assets/Prefabs/UI/MatchHUD.prefab";
    const string CirclePath = "Assets/Art/UI/HudCircle.png";
    const string CapPath    = "Assets/Art/UI/HudCap.png";
    const string StripePath = "Assets/Art/UI/HudStripes.png";
    const string WavePath   = "Assets/Art/UI/HudWave.png";
    const string ArrowPath  = "Assets/Art/UI/HudArrow.png";
    const string WavyFillMatPath = "Assets/Art/UI/WavyFill.mat";
    const string RingPath   = "Assets/Art/UI/HudRing.png";
    const string OutlineMat = "Assets/TextMesh Pro/Resources/Fonts & Materials/LiberationSans SDF - Outline.mat";
    const int SlotSize = 64, SlotGap = 12, TimerWidth = 180;
    const float BarWidth = 760, BarHeight = 36;
    // Cap corners are elliptical, narrower than tall, so the ends are flatter than a pill's.
    const int CapRadiusX = 18, CapRadiusY = 32;
    // The stripe tile holds 45° stripes; stretching it vertically by 1/tan(angle) tilts them to
    // StripeAngle from vertical, rising to the right.
    const int StripesPerTile = 4;
    const float StripeSpacing = 26, StripeAngle = 30; // UI px between stripes along the bar, degrees
    // Each fill's leading edge is a sine wave WaveDepth px deep; the beta wave is the complement of
    // the alpha one, so the two interlock where they meet.
    const float WaveDepth = 7, WaveLength = 40;
    // Map: right of centre and below the turf bar, no frame (it renders over a transparent
    // background); team list on the left.
    const float MapWidth = 1000, MapHeight = 860, MapX = 230, MapY = -70;
    const float ListX = -540, EntryWidth = 300, EntryHeight = 84, EntryGap = 18;

    static readonly Color PanelColour  = new Color(0.06f, 0.06f, 0.09f, 0.93f);
    static readonly Color ButtonColour = new Color(0.2f, 0.2f, 0.26f, 1f);

    static Sprite circle, rounded, cap, arrow, ring;
    static Material outline;

    [MenuItem("Tools/UI/Build Match HUD Prefab")]
    public static void Build()
    {
        circle  = EnsureCircleSprite();
        cap     = EnsureCapSprite();
        arrow   = EnsureArrowSprite();
        ring    = EnsureRingSprite();
        rounded = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/UISprite.psd");
        outline = AssetDatabase.LoadAssetAtPath<Material>(OutlineMat);

        Scene preview = EditorSceneManager.NewPreviewScene();
        GameObject root = new GameObject("MatchHUD", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(CanvasGroup));
        SceneManager.MoveGameObjectToScene(root, preview);

        Canvas canvas = root.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 10;
        CanvasScaler scaler = root.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080);
        scaler.matchWidthOrHeight = 1f; // same as the existing HUD canvas
        CanvasGroup group = root.GetComponent<CanvasGroup>();
        group.interactable = false;
        group.blocksRaycasts = false; // the HUD never takes clicks; the host menu has its own canvas

        MatchHUD hud = BuildHud(root);
        BuildLoadoutHud(root);
        BuildHostMenu(root, hud);
        BuildMapScreen(root);

        Directory.CreateDirectory(Path.GetDirectoryName(PrefabPath));
        PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
        EditorSceneManager.ClosePreviewScene(preview);
        Debug.Log($"[MatchHUD] Built {PrefabPath}");
    }

    // ── HUD ────────────────────────────────────────────────────────────────

    static MatchHUD BuildHud(GameObject root)
    {
        RectTransform top = Rect("Top", root.transform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0, -18), new Vector2(1100, 180));

        RectTransform timerBg = Rect("Timer", top, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), Vector2.zero, new Vector2(TimerWidth, 72));
        Img(timerBg.gameObject, rounded, new Color(0f, 0f, 0f, 0.55f), sliced: true);
        TextMeshProUGUI timer = Text("TimerText", timerBg, "3:00", 50, Color.white, TextAlignmentOptions.Center, true);
        Stretch(timer.rectTransform);

        float teamWidth = 4 * SlotSize + 3 * SlotGap;
        float teamOffset = TimerWidth / 2f + 24;
        RectTransform alphaTeam = Rect("AlphaTeam", top, new Vector2(0.5f, 1f), new Vector2(1f, 1f), new Vector2(-teamOffset, -4), new Vector2(teamWidth, SlotSize));
        RectTransform betaTeam  = Rect("BetaTeam",  top, new Vector2(0.5f, 1f), new Vector2(0f, 1f), new Vector2(teamOffset, -4), new Vector2(teamWidth, SlotSize));
        var alphaSlots = new MatchHUD.PlayerSlot[4];
        var betaSlots  = new MatchHUD.PlayerSlot[4];
        for (int i = 0; i < 4; i++)
        {
            alphaSlots[i] = HudSlot($"Slot{i}", alphaTeam, i);
            betaSlots[i]  = HudSlot($"Slot{i}", betaTeam, i);
        }

        // Turf bar: a tube with flattened rounded ends. The track masks the fills, stripes and
        // shading to its shape; a darker copy behind it is the outline.
        RectTransform bar = Rect("TurfBar", top, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0, -112), new Vector2(BarWidth, BarHeight));
        RectTransform frame = Rect("Frame", bar, Vector2.zero, new Vector2(0.5f, 0.5f), Vector2.zero, Vector2.zero);
        StretchInset(frame, -5);
        CapImg(frame.gameObject, new Color(0f, 0f, 0f, 0.7f), BarHeight + 10);
        RectTransform track = Rect("Track", bar, Vector2.zero, new Vector2(0.5f, 0.5f), Vector2.zero, Vector2.zero);
        Stretch(track);
        CapImg(track.gameObject, new Color(0.24f, 0.24f, 0.29f, 0.95f), BarHeight);
        track.gameObject.AddComponent<Mask>().showMaskGraphic = true;

        RectTransform alphaFill = Rect("AlphaFill", track, Vector2.zero, new Vector2(0f, 0.5f), Vector2.zero, Vector2.zero);
        alphaFill.anchorMin = new Vector2(0f, 0f); alphaFill.anchorMax = new Vector2(0f, 1f);
        alphaFill.offsetMin = Vector2.zero; alphaFill.offsetMax = new Vector2(-WaveDepth / 2f, 0f);
        Img(alphaFill.gameObject, null, Color.cyan, sliced: false);
        RawImage alphaWave = Wave("Wave", alphaFill, 1f, new Rect(0f, 0f, 1f, BarHeight / WaveLength), Color.cyan);
        RectTransform betaFill = Rect("BetaFill", track, Vector2.zero, new Vector2(1f, 0.5f), Vector2.zero, Vector2.zero);
        betaFill.anchorMin = new Vector2(1f, 0f); betaFill.anchorMax = new Vector2(1f, 1f);
        betaFill.offsetMin = new Vector2(WaveDepth / 2f, 0f); betaFill.offsetMax = Vector2.zero;
        Img(betaFill.gameObject, null, Color.magenta, sliced: false);
        RawImage betaWave = Wave("Wave", betaFill, 0f, new Rect(1f, 0.5f, -1f, BarHeight / WaveLength), Color.magenta); // mirrored, half a period on

        // Diagonal stripes over everything, StripeSpacing apart along the bar.
        RectTransform stripes = Rect("Stripes", track, Vector2.zero, new Vector2(0.5f, 0.5f), Vector2.zero, Vector2.zero);
        Stretch(stripes);
        RawImage stripeImg = stripes.gameObject.AddComponent<RawImage>();
        stripeImg.texture = EnsureStripeTexture();
        stripeImg.color = new Color(1f, 1f, 1f, 0.16f);
        stripeImg.raycastTarget = false;
        float tileW = StripeSpacing * StripesPerTile; // UI px covered by one texture tile
        float tileH = tileW / Mathf.Tan(StripeAngle * Mathf.Deg2Rad);
        stripeImg.uvRect = new Rect(0, 0, BarWidth / tileW, BarHeight / tileH);

        // Tube shading: darker underside, glossy highlight along the top.
        RectTransform shade = Rect("Shade", track, Vector2.zero, new Vector2(0.5f, 0.5f), Vector2.zero, Vector2.zero);
        shade.anchorMin = new Vector2(0f, 0f); shade.anchorMax = new Vector2(1f, 0.38f);
        shade.offsetMin = shade.offsetMax = Vector2.zero;
        Img(shade.gameObject, null, new Color(0f, 0f, 0f, 0.2f), sliced: false);
        RectTransform gloss = Rect("Gloss", track, Vector2.zero, new Vector2(0.5f, 0.5f), Vector2.zero, Vector2.zero);
        gloss.anchorMin = new Vector2(0f, 0.58f); gloss.anchorMax = new Vector2(1f, 0.86f);
        gloss.offsetMin = new Vector2(12, 0); gloss.offsetMax = new Vector2(-12, 0);
        CapImg(gloss.gameObject, new Color(1f, 1f, 1f, 0.24f), BarHeight * 0.28f);

        // Percentages (hidden by default; toggled from the host menu).
        RectTransform pct = Rect("Percentages", bar, Vector2.zero, new Vector2(0.5f, 0.5f), Vector2.zero, Vector2.zero);
        Stretch(pct);
        TextMeshProUGUI alphaPct = Text("AlphaPercent", pct, "0.0%", 22, Color.white, TextAlignmentOptions.Left, true);
        Place(alphaPct.rectTransform, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(18, 0), new Vector2(120, 32));
        TextMeshProUGUI betaPct = Text("BetaPercent", pct, "0.0%", 22, Color.white, TextAlignmentOptions.Right, true);
        Place(betaPct.rectTransform, new Vector2(1f, 0.5f), new Vector2(1f, 0.5f), new Vector2(-18, 0), new Vector2(120, 32));
        TextMeshProUGUI neutralPct = Text("NeutralPercent", pct, "100.0%", 18, new Color(0.92f, 0.92f, 0.95f), TextAlignmentOptions.Center, false);
        Place(neutralPct.rectTransform, new Vector2(0.5f, 0f), new Vector2(0.5f, 1f), new Vector2(0, -8), new Vector2(140, 26)); // below the bar
        pct.gameObject.SetActive(false);

        MatchHUD hud = root.AddComponent<MatchHUD>();
        SerializedObject so = new SerializedObject(hud);
        so.FindProperty("timerText").objectReferenceValue = timer;
        so.FindProperty("alphaFill").objectReferenceValue = alphaFill;
        so.FindProperty("betaFill").objectReferenceValue = betaFill;
        so.FindProperty("alphaWave").objectReferenceValue = alphaWave;
        so.FindProperty("betaWave").objectReferenceValue = betaWave;
        so.FindProperty("percentLabels").objectReferenceValue = pct.gameObject;
        so.FindProperty("alphaPercent").objectReferenceValue = alphaPct;
        so.FindProperty("betaPercent").objectReferenceValue = betaPct;
        so.FindProperty("neutralPercent").objectReferenceValue = neutralPct;
        AssignHudSlots(so.FindProperty("alphaSlots"), alphaSlots);
        AssignHudSlots(so.FindProperty("betaSlots"), betaSlots);
        so.ApplyModifiedPropertiesWithoutUndo();
        return hud;
    }

    static MatchHUD.PlayerSlot HudSlot(string name, RectTransform team, int index)
    {
        RectTransform slotRect = Rect(name, team, new Vector2(0f, 0.5f), new Vector2(0.5f, 0.5f),
                                      new Vector2(SlotSize / 2f + index * (SlotSize + SlotGap), 0), new Vector2(SlotSize, SlotSize));
        Image icon = Img(slotRect.gameObject, circle, new Color(0f, 0f, 0f, 0.35f), sliced: false);
        Outline localRing = slotRect.gameObject.AddComponent<Outline>();
        localRing.effectColor = Color.white;
        localRing.effectDistance = new Vector2(3, -3);
        localRing.enabled = false;

        TextMeshProUGUI initial = Text("Initial", slotRect, "", 30, new Color(0.08f, 0.08f, 0.12f), TextAlignmentOptions.Center, true, useOutline: false);
        Stretch(initial.rectTransform);

        TextMeshProUGUI dead = Text("Dead", slotRect, "X", 54, new Color(1f, 1f, 1f, 0.95f), TextAlignmentOptions.Center, true);
        Stretch(dead.rectTransform);
        dead.gameObject.SetActive(false);

        RectTransform special = Rect("SpecialReady", slotRect, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(SlotSize + 16, SlotSize + 16));
        Img(special.gameObject, ring, new Color(1f, 0.93f, 0.5f), sliced: false);
        special.gameObject.SetActive(false);

        TextMeshProUGUI playerName = Text("Name", slotRect, "", 15, Color.white, TextAlignmentOptions.Center, false);
        Place(playerName.rectTransform, new Vector2(0.5f, 0f), new Vector2(0.5f, 1f), new Vector2(0, -4), new Vector2(SlotSize + SlotGap, 20));
        playerName.textWrappingMode = TextWrappingModes.NoWrap;
        playerName.overflowMode = TextOverflowModes.Ellipsis;

        return new MatchHUD.PlayerSlot { icon = icon, initial = initial, playerName = playerName, deadMark = dead.gameObject, localMark = localRing, specialMark = special };
    }

    static void AssignHudSlots(SerializedProperty array, MatchHUD.PlayerSlot[] slots)
    {
        array.arraySize = slots.Length;
        for (int i = 0; i < slots.Length; i++)
        {
            SerializedProperty e = array.GetArrayElementAtIndex(i);
            e.FindPropertyRelative("icon").objectReferenceValue = slots[i].icon;
            e.FindPropertyRelative("initial").objectReferenceValue = slots[i].initial;
            e.FindPropertyRelative("playerName").objectReferenceValue = slots[i].playerName;
            e.FindPropertyRelative("deadMark").objectReferenceValue = slots[i].deadMark;
            e.FindPropertyRelative("localMark").objectReferenceValue = slots[i].localMark;
            e.FindPropertyRelative("specialMark").objectReferenceValue = slots[i].specialMark;
        }
    }

    // ── Sub / special gauges (top right) ───────────────────────────────────

    static void BuildLoadoutHud(GameObject root)
    {
        Material wavy = AssetDatabase.LoadAssetAtPath<Material>(WavyFillMatPath);
        if (wavy == null)
        {
            wavy = new Material(Shader.Find("UI/WavyFill"));
            AssetDatabase.CreateAsset(wavy, WavyFillMatPath);
        }

        RectTransform group = Rect("Loadout", root.transform, Vector2.one, Vector2.one, new Vector2(-36, -28), new Vector2(280, 200));

        // Special: the big gauge in the corner.
        Image specialFill = Gauge("Special", group, new Vector2(0, 0), 150, wavy, out RectTransform specialFrame);
        TextMeshProUGUI ready = Text("Ready", specialFrame, "READY", 30, Color.white, TextAlignmentOptions.Center, true);
        Stretch(ready.rectTransform);
        ready.rectTransform.anchoredPosition = new Vector2(-114, 54); // up and left of the gauge
        ready.gameObject.SetActive(false);
        // Names stand in for icons; key hints sit on the gauges' outer lower corners.
        TextMeshProUGUI specialIcon = Text("Icon", specialFrame, "BUBBLE", 17, Color.white, TextAlignmentOptions.Center, true);
        Place(specialIcon.rectTransform, new Vector2(0.5f, 0f), new Vector2(0.5f, 1f), new Vector2(2.4f, 88), new Vector2(170, 24));
        TextMeshProUGUI specialKey = Text("Key", specialFrame, "[Q]", 17, Color.white, TextAlignmentOptions.Center, true);
        Place(specialKey.rectTransform, new Vector2(0.5f, 0f), new Vector2(0.5f, 1f), new Vector2(-61.2f, 19.2f), new Vector2(33.25f, 24));

        // Sub: smaller, overlapping the special's lower right, with a line at the ink it costs.
        Image subFill = Gauge("Sub", group, new Vector2(24.7f, -98.4f), 92, wavy, out RectTransform subFrame);
        RectTransform mark = Rect("CostMark", subFill.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(64, 3));
        Img(mark.gameObject, null, new Color(1f, 1f, 1f, 0.85f), sliced: false);
        TextMeshProUGUI subKey = Text("Key", subFrame, "[E]", 15, Color.white, TextAlignmentOptions.Center, true);
        Place(subKey.rectTransform, new Vector2(0.5f, 0f), new Vector2(0.5f, 1f), new Vector2(32.4f, 19.2f), new Vector2(34.6f, 22));
        TextMeshProUGUI subIcon = Text("Icon", subFrame, "BEACON", 15, Color.white, TextAlignmentOptions.Center, true);
        Place(subIcon.rectTransform, new Vector2(0.5f, 0f), new Vector2(0.5f, 1f), new Vector2(3.1f, 56.5f), new Vector2(74.8f, 22));

        LoadoutHUD hud = root.AddComponent<LoadoutHUD>();
        var so = new SerializedObject(hud);
        so.FindProperty("root").objectReferenceValue = group.gameObject;
        so.FindProperty("subFill").objectReferenceValue = subFill;
        so.FindProperty("specialFill").objectReferenceValue = specialFill;
        so.FindProperty("subCostMark").objectReferenceValue = mark;
        so.FindProperty("specialFrame").objectReferenceValue = specialFrame;
        so.FindProperty("specialReady").objectReferenceValue = ready.gameObject;
        so.FindProperty("subLabel").objectReferenceValue = subIcon;
        so.ApplyModifiedPropertiesWithoutUndo();
    }

    // Dark circle with a liquid fill inset in it; returns the fill.
    static Image Gauge(string name, RectTransform parent, Vector2 pos, float size, Material wavy, out RectTransform frame)
    {
        frame = Rect(name, parent, Vector2.one, Vector2.one, pos, new Vector2(size, size));
        Img(frame.gameObject, circle, new Color(0f, 0f, 0f, 0.6f), sliced: false);
        float inner = size * 0.86f;
        RectTransform fillRt = Rect("Fill", frame, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(inner, inner));
        Image fill = Img(fillRt.gameObject, circle, Color.cyan, sliced: false);
        fill.material = wavy;
        return fill;
    }

    // ── Host menu ──────────────────────────────────────────────────────────

    static void BuildHostMenu(GameObject root, MatchHUD hud)
    {
        // Own canvas + raycaster, ignoring the HUD's non-interactive CanvasGroup.
        RectTransform menuRoot = Rect("HostMenu", root.transform, Vector2.zero, new Vector2(0.5f, 0.5f), Vector2.zero, Vector2.zero);
        Stretch(menuRoot);
        Canvas menuCanvas = menuRoot.gameObject.AddComponent<Canvas>();
        menuCanvas.overrideSorting = true;
        menuCanvas.sortingOrder = 20;
        menuRoot.gameObject.AddComponent<GraphicRaycaster>();
        CanvasGroup menuGroup = menuRoot.gameObject.AddComponent<CanvasGroup>();
        menuGroup.ignoreParentGroups = true;

        // Main panel.
        RectTransform main = Panel("MainPanel", menuRoot, new Vector2(440, 480));
        Title(main, "HOST MENU");
        Button reset   = MenuButton("ResetMap",    main, "Reset map",        new Vector2(0, -100), new Vector2(360, 58), out _);
        Button teams   = MenuButton("EditTeams",   main, "Edit teams",       new Vector2(0, -170), new Vector2(360, 58), out _);
        Button percent = MenuButton("Percentages", main, "Show percentages", new Vector2(0, -240), new Vector2(360, 58), out TextMeshProUGUI percentLabel);
        Button start   = MenuButton("StartGame",   main, "Start game",       new Vector2(0, -310), new Vector2(360, 58), out _);
        Button close   = MenuButton("Close",       main, "Close",            new Vector2(0, -392), new Vector2(180, 46), out _);
        TextMeshProUGUI hint = Text("Hint", main, "G / Esc to close", 16, new Color(1f, 1f, 1f, 0.5f), TextAlignmentOptions.Center, false, useOutline: false);
        Place(hint.rectTransform, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0, 10), new Vector2(300, 24));

        // Team editor.
        RectTransform teamPanel = Panel("TeamPanel", menuRoot, new Vector2(1180, 600));
        Title(teamPanel, "EDIT TEAMS");
        TextMeshProUGUI teamHint = Text("Hint", teamPanel, "Click a player, then a slot to move them (swaps if it's taken)", 18,
                                        new Color(1f, 1f, 1f, 0.6f), TextAlignmentOptions.Center, false, useOutline: false);
        Place(teamHint.rectTransform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0, -68), new Vector2(900, 26));

        string[] headers = { "ALPHA", "BETA", "SPECTATE", "NOT PLAYING" };
        Color[] headerColours = { Color.cyan, Color.magenta, new Color(0.85f, 0.85f, 0.9f), new Color(0.6f, 0.6f, 0.65f) };
        int[] counts = { 4, 4, 2, 2 };
        float[] columnX = { -435, -145, 145, 435 };
        var columns = new HostMenu.SlotButton[4][];
        for (int c = 0; c < 4; c++)
        {
            TextMeshProUGUI header = Text("Header" + c, teamPanel, headers[c], 24, headerColours[c], TextAlignmentOptions.Center, true);
            Place(header.rectTransform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(columnX[c], -112), new Vector2(260, 32));
            columns[c] = new HostMenu.SlotButton[counts[c]];
            for (int s = 0; s < counts[c]; s++)
            {
                Button b = MenuButton($"{headers[c]}_{s}", teamPanel, "—", new Vector2(columnX[c], -156 - s * 66), new Vector2(250, 54), out TextMeshProUGUI label);
                label.fontSize = 20;
                label.fontStyle = FontStyles.Normal;
                columns[c][s] = new HostMenu.SlotButton { button = b, background = b.GetComponent<Image>(), label = label };
            }
        }
        Button back = MenuButton("Back", teamPanel, "Back", new Vector2(0, -526), new Vector2(180, 46), out _);

        HostMenu menu = menuRoot.gameObject.AddComponent<HostMenu>();
        SerializedObject so = new SerializedObject(menu);
        so.FindProperty("hud").objectReferenceValue = hud;
        so.FindProperty("mainPanel").objectReferenceValue = main.gameObject;
        so.FindProperty("teamPanel").objectReferenceValue = teamPanel.gameObject;
        so.FindProperty("resetButton").objectReferenceValue = reset;
        so.FindProperty("teamsButton").objectReferenceValue = teams;
        so.FindProperty("percentButton").objectReferenceValue = percent;
        so.FindProperty("startButton").objectReferenceValue = start;
        so.FindProperty("closeButton").objectReferenceValue = close;
        so.FindProperty("teamsBackButton").objectReferenceValue = back;
        so.FindProperty("percentButtonLabel").objectReferenceValue = percentLabel;
        AssignMenuSlots(so.FindProperty("alphaSlots"), columns[0]);
        AssignMenuSlots(so.FindProperty("betaSlots"), columns[1]);
        AssignMenuSlots(so.FindProperty("spectateSlots"), columns[2]);
        AssignMenuSlots(so.FindProperty("benchSlots"), columns[3]);
        so.ApplyModifiedPropertiesWithoutUndo();

        main.gameObject.SetActive(false);
        teamPanel.gameObject.SetActive(false);
    }

    // ── Map (hold Tab) ─────────────────────────────────────────────────────

    static void BuildMapScreen(GameObject root)
    {
        // Own canvas + raycaster like the host menu, sorted under it.
        RectTransform mapRoot = Rect("MapScreen", root.transform, Vector2.zero, new Vector2(0.5f, 0.5f), Vector2.zero, Vector2.zero);
        Stretch(mapRoot);
        Canvas mapCanvas = mapRoot.gameObject.AddComponent<Canvas>();
        mapCanvas.overrideSorting = true;
        mapCanvas.sortingOrder = 15;
        mapRoot.gameObject.AddComponent<GraphicRaycaster>();
        mapRoot.gameObject.AddComponent<CanvasGroup>().ignoreParentGroups = true;

        RectTransform screen = Rect("Screen", mapRoot, Vector2.zero, new Vector2(0.5f, 0.5f), Vector2.zero, Vector2.zero);
        Stretch(screen);
        RectTransform backdrop = Rect("Backdrop", screen, Vector2.zero, new Vector2(0.5f, 0.5f), Vector2.zero, Vector2.zero);
        Stretch(backdrop);
        Img(backdrop.gameObject, null, new Color(0.02f, 0.02f, 0.04f, 0.72f), sliced: false).raycastTarget = true;

        // Map, then the lines over it, then markers over the lines. Markers share the map's rect.
        Vector2 centre = new Vector2(0.5f, 0.5f);
        Vector2 mapPos = new Vector2(MapX, MapY), mapSize = new Vector2(MapWidth, MapHeight);
        RectTransform mapRt = Rect("MapImage", screen, centre, centre, mapPos, mapSize);
        RawImage mapImage = mapRt.gameObject.AddComponent<RawImage>();
        mapImage.raycastTarget = false;
        TextMeshProUGUI hint = Text("Hint", screen, "Click a teammate, beacon or spawn to Super Jump   ·   release TAB to close", 18,
                                    new Color(1f, 1f, 1f, 0.6f), TextAlignmentOptions.Center, false, useOutline: false);
        Place(hint.rectTransform, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(MapX, 12), new Vector2(MapWidth, 26));

        RectTransform lines = Rect("Lines", screen, Vector2.zero, centre, Vector2.zero, Vector2.zero);
        Stretch(lines);
        RectTransform markers = Rect("Markers", screen, centre, centre, mapPos, mapSize);
        Button beaconTemplate = BeaconMarker(markers); // first, so beacons draw under players

        float listHeight = 4 * EntryHeight + 3 * EntryGap;
        RectTransform list = Rect("TeamList", screen, centre, centre, new Vector2(ListX, MapY), new Vector2(EntryWidth, listHeight));
        TextMeshProUGUI header = Text("Header", list, "SUPER JUMP", 26, Color.white, TextAlignmentOptions.Left, true);
        Place(header.rectTransform, new Vector2(0f, 1f), Vector2.zero, new Vector2(4, 10), new Vector2(EntryWidth, 34));

        var entries = new MapScreen.Entry[4];
        for (int i = 0; i < 4; i++) entries[i] = MapEntry(i, list, lines, markers, circle);
        MapScreen.Entry spawn = MapEntry(4, list, lines, markers, rounded, extraGap: 14); // square marker: not a player
        spawn.initial.text = "S"; // stands in for an icon
        spawn.playerName.text = "Spawn";
        spawn.status.text = "Super Jump";
        spawn.facing.gameObject.SetActive(false);

        MapScreen map = mapRoot.gameObject.AddComponent<MapScreen>();
        SerializedObject so = new SerializedObject(map);
        so.FindProperty("screen").objectReferenceValue = screen.gameObject;
        so.FindProperty("mapImage").objectReferenceValue = mapImage;
        so.FindProperty("markerArea").objectReferenceValue = markers;
        so.FindProperty("lineLayer").objectReferenceValue = lines;
        so.FindProperty("beaconMarkerTemplate").objectReferenceValue = beaconTemplate;
        SerializedProperty array = so.FindProperty("entries");
        array.arraySize = entries.Length;
        for (int i = 0; i < entries.Length; i++) AssignMapEntry(array.GetArrayElementAtIndex(i), entries[i]);
        AssignMapEntry(so.FindProperty("spawnEntry"), spawn);
        so.ApplyModifiedPropertiesWithoutUndo();
        screen.gameObject.SetActive(false);
    }

    // Diamond in the team colour inside a white one; MapScreen clones it per beacon.
    static Button BeaconMarker(RectTransform markers)
    {
        Vector2 centre = new Vector2(0.5f, 0.5f);
        RectTransform rt = Rect("BeaconMarker", markers, centre, centre, Vector2.zero, new Vector2(30, 30));
        rt.localRotation = Quaternion.Euler(0f, 0f, 45f);
        Image ring = Img(rt.gameObject, rounded, Color.white, sliced: true);
        ring.raycastTarget = true;
        RectTransform icon = Rect("Icon", rt, centre, centre, Vector2.zero, new Vector2(20, 20));
        Img(icon.gameObject, rounded, Color.cyan, sliced: true);
        Button button = rt.gameObject.AddComponent<Button>();
        button.targetGraphic = ring;
        ColorBlock cb = button.colors;
        cb.highlightedColor = new Color(1f, 0.85f, 0.4f);
        button.colors = cb;
        rt.gameObject.SetActive(false);
        return button;
    }

    static void AssignMapEntry(SerializedProperty e, MapScreen.Entry entry)
    {
        e.FindPropertyRelative("button").objectReferenceValue = entry.button;
        e.FindPropertyRelative("icon").objectReferenceValue = entry.icon;
        e.FindPropertyRelative("initial").objectReferenceValue = entry.initial;
        e.FindPropertyRelative("playerName").objectReferenceValue = entry.playerName;
        e.FindPropertyRelative("status").objectReferenceValue = entry.status;
        e.FindPropertyRelative("lineStart").objectReferenceValue = entry.lineStart;
        e.FindPropertyRelative("line").objectReferenceValue = entry.line;
        e.FindPropertyRelative("marker").objectReferenceValue = entry.marker;
        e.FindPropertyRelative("markerIcon").objectReferenceValue = entry.markerIcon;
        e.FindPropertyRelative("markerInitial").objectReferenceValue = entry.markerInitial;
        e.FindPropertyRelative("facing").objectReferenceValue = entry.facing;
        e.FindPropertyRelative("entryHover").objectReferenceValue = entry.entryHover;
        e.FindPropertyRelative("markerHover").objectReferenceValue = entry.markerHover;
    }

    // A list entry, its line, and its map marker (in the given shape).
    static MapScreen.Entry MapEntry(int index, RectTransform list, RectTransform lines, RectTransform markers, Sprite shape, float extraGap = 0f)
    {
        Vector2 centre = new Vector2(0.5f, 0.5f);
        RectTransform rt = Rect($"Entry{index}", list, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f),
                                new Vector2(0, -index * (EntryHeight + EntryGap) - extraGap), new Vector2(EntryWidth, EntryHeight));
        Image bg = Img(rt.gameObject, rounded, new Color(0.3f, 0.3f, 0.38f, 0.95f), sliced: true);
        bg.raycastTarget = true;
        Button button = rt.gameObject.AddComponent<Button>();
        button.targetGraphic = bg;
        ColorBlock cb = button.colors; // tints darken the light background; hover brightens it
        cb.normalColor = cb.disabledColor = cb.selectedColor = new Color(0.5f, 0.5f, 0.5f, 1f);
        cb.highlightedColor = new Color(0.85f, 0.85f, 0.9f, 1f);
        cb.pressedColor = Color.white;
        button.colors = cb;

        RectTransform iconRt = Rect("Icon", rt, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(12, 0), new Vector2(60, 60));
        Image icon = Img(iconRt.gameObject, circle, new Color(0f, 0f, 0f, 0.35f), sliced: false);
        TextMeshProUGUI initial = Text("Initial", iconRt, "", 30, new Color(0.08f, 0.08f, 0.12f), TextAlignmentOptions.Center, true, useOutline: false);
        Stretch(initial.rectTransform);
        TextMeshProUGUI playerName = Text("Name", rt, "—", 24, Color.white, TextAlignmentOptions.Left, true);
        Place(playerName.rectTransform, new Vector2(0f, 0.5f), Vector2.zero, new Vector2(86, -2), new Vector2(EntryWidth - 100, 32));
        playerName.textWrappingMode = TextWrappingModes.NoWrap;
        playerName.overflowMode = TextOverflowModes.Ellipsis;
        TextMeshProUGUI status = Text("Status", rt, "", 18, Color.white, TextAlignmentOptions.Left, false, useOutline: false);
        Place(status.rectTransform, new Vector2(0f, 0.5f), new Vector2(0f, 1f), new Vector2(86, -2), new Vector2(EntryWidth - 100, 24));
        RectTransform lineStart = Rect("LineStart", rt, new Vector2(1f, 0.5f), centre, Vector2.zero, Vector2.zero);

        RectTransform line = Rect($"Line{index}", lines, centre, centre, Vector2.zero, new Vector2(10, 3));
        Img(line.gameObject, null, Color.white, sliced: false);

        // Marker: white ring, team-coloured disc with the initial, and a facing arrow for the local player.
        RectTransform m = Rect($"Marker{index}", markers, centre, centre, Vector2.zero, new Vector2(52, 52));
        Img(m.gameObject, shape, Color.white, sliced: shape == rounded).raycastTarget = true;
        Button marker = m.gameObject.AddComponent<Button>();
        marker.transition = Selectable.Transition.None; // hover is shown by scaling (see MapScreen)
        RectTransform discRt = Rect("Icon", m, centre, centre, Vector2.zero, new Vector2(42, 42));
        Image disc = Img(discRt.gameObject, shape, Color.cyan, sliced: shape == rounded);
        TextMeshProUGUI markerInitial = Text("Initial", discRt, "", 22, new Color(0.08f, 0.08f, 0.12f), TextAlignmentOptions.Center, true, useOutline: false);
        Stretch(markerInitial.rectTransform);
        RectTransform facing = Rect("Facing", m, centre, centre, Vector2.zero, Vector2.zero);
        RectTransform arrowRt = Rect("Arrow", facing, centre, new Vector2(0.5f, 0f), new Vector2(0, 28), new Vector2(22, 18));
        Img(arrowRt.gameObject, arrow, Color.white, sliced: false);

        return new MapScreen.Entry
        {
            button = button, icon = icon, initial = initial, playerName = playerName, status = status,
            lineStart = lineStart, line = line, marker = marker, markerIcon = disc, markerInitial = markerInitial, facing = facing,
            entryHover = rt.gameObject.AddComponent<HoverFlag>(), markerHover = m.gameObject.AddComponent<HoverFlag>(),
        };
    }

    static RectTransform Panel(string name, RectTransform parent, Vector2 size)
    {
        RectTransform panel = Rect(name, parent, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, size);
        Img(panel.gameObject, rounded, PanelColour, sliced: true).raycastTarget = true; // swallow clicks behind it
        return panel;
    }

    static void Title(RectTransform panel, string text)
    {
        TextMeshProUGUI title = Text("Title", panel, text, 34, Color.white, TextAlignmentOptions.Center, true);
        Place(title.rectTransform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0, -18), new Vector2(600, 44));
    }

    static Button MenuButton(string name, RectTransform parent, string text, Vector2 pos, Vector2 size, out TextMeshProUGUI label)
    {
        RectTransform rt = Rect(name, parent, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), pos, size);
        Image bg = Img(rt.gameObject, rounded, ButtonColour, sliced: true);
        bg.raycastTarget = true;
        Button button = rt.gameObject.AddComponent<Button>();
        button.targetGraphic = bg;
        ColorBlock cb = button.colors;
        cb.normalColor = Color.white;
        cb.highlightedColor = new Color(0.8f, 0.88f, 1f);
        cb.pressedColor = new Color(0.65f, 0.7f, 0.8f);
        cb.selectedColor = Color.white;
        button.colors = cb;
        label = Text("Label", rt, text, 24, Color.white, TextAlignmentOptions.Center, true, useOutline: false);
        Stretch(label.rectTransform);
        label.textWrappingMode = TextWrappingModes.NoWrap;
        label.overflowMode = TextOverflowModes.Ellipsis;
        return button;
    }

    static void AssignMenuSlots(SerializedProperty array, HostMenu.SlotButton[] slots)
    {
        array.arraySize = slots.Length;
        for (int i = 0; i < slots.Length; i++)
        {
            SerializedProperty e = array.GetArrayElementAtIndex(i);
            e.FindPropertyRelative("button").objectReferenceValue = slots[i].button;
            e.FindPropertyRelative("background").objectReferenceValue = slots[i].background;
            e.FindPropertyRelative("label").objectReferenceValue = slots[i].label;
        }
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    static RectTransform Rect(string name, Transform parent, Vector2 anchor, Vector2 pivot, Vector2 pos, Vector2 size)
    {
        RectTransform rt = new GameObject(name, typeof(RectTransform)).GetComponent<RectTransform>();
        rt.SetParent(parent, false);
        Place(rt, anchor, pivot, pos, size);
        return rt;
    }

    static void Place(RectTransform rt, Vector2 anchor, Vector2 pivot, Vector2 pos, Vector2 size)
    {
        rt.anchorMin = rt.anchorMax = anchor;
        rt.pivot = pivot;
        rt.anchoredPosition = pos;
        rt.sizeDelta = size;
    }

    static void Stretch(RectTransform rt) => StretchInset(rt, 0);

    static void StretchInset(RectTransform rt, float inset)
    {
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = new Vector2(inset, inset);
        rt.offsetMax = new Vector2(-inset, -inset);
    }

    static Image Img(GameObject go, Sprite sprite, Color colour, bool sliced)
    {
        Image img = go.AddComponent<Image>();
        img.sprite = sprite;
        img.type = sliced ? Image.Type.Sliced : Image.Type.Simple;
        img.color = colour;
        img.raycastTarget = false;
        return img;
    }

    // Cap-cornered image; the multiplier scales the sprite's borders so the ends fit `height`.
    static Image CapImg(GameObject go, Color colour, float height)
    {
        Image img = Img(go, cap, colour, sliced: true);
        img.pixelsPerUnitMultiplier = 2f * CapRadiusY / height;
        return img;
    }

    // Wave strip on one side of a fill (side 1 = right edge, 0 = left), WaveDepth wide.
    static RawImage Wave(string name, RectTransform fill, float side, Rect uv, Color colour)
    {
        RectTransform rt = Rect(name, fill, Vector2.zero, new Vector2(1f - side, 0.5f), Vector2.zero, Vector2.zero);
        rt.anchorMin = new Vector2(side, 0f); rt.anchorMax = new Vector2(side, 1f);
        rt.anchoredPosition = Vector2.zero;
        rt.sizeDelta = new Vector2(WaveDepth, 0f);
        RawImage img = rt.gameObject.AddComponent<RawImage>();
        img.texture = EnsureWaveTexture();
        img.uvRect = uv;
        img.color = colour;
        img.raycastTarget = false;
        return img;
    }

    static TextMeshProUGUI Text(string name, Transform parent, string text, float size, Color colour,
                                TextAlignmentOptions align, bool bold, bool useOutline = true)
    {
        TextMeshProUGUI t = new GameObject(name, typeof(RectTransform)).AddComponent<TextMeshProUGUI>();
        t.transform.SetParent(parent, false);
        t.text = text;
        t.fontSize = size;
        t.color = colour;
        t.alignment = align;
        t.fontStyle = bold ? FontStyles.Bold : FontStyles.Normal;
        t.raycastTarget = false;
        if (useOutline && outline != null) t.fontSharedMaterial = outline; // readable over bright scenes
        return t;
    }

    // Smooth white circle used for icons and caps (the built-in knob sprite is tiny and blurs).
    static Sprite EnsureCircleSprite()
    {
        if (!File.Exists(CirclePath))
        {
            const int size = 256;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            float r = size / 2f - 2f, c = (size - 1) / 2f;
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float d = Mathf.Sqrt((x - c) * (x - c) + (y - c) * (y - c));
                tex.SetPixel(x, y, new Color(1f, 1f, 1f, Mathf.Clamp01(r - d + 0.5f))); // 1px anti-aliased edge
            }
            Directory.CreateDirectory(Path.GetDirectoryName(CirclePath));
            File.WriteAllBytes(CirclePath, tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
            AssetDatabase.ImportAsset(CirclePath);
        }

        var importer = (TextureImporter)AssetImporter.GetAtPath(CirclePath);
        if (importer.textureType != TextureImporterType.Sprite || importer.spriteImportMode != SpriteImportMode.Single)
        {
            importer.textureType = TextureImporterType.Sprite;
            importer.spriteImportMode = SpriteImportMode.Single; // the project default here is Multiple, which yields no sprite
            importer.alphaIsTransparency = true;
            importer.mipmapEnabled = false;
            importer.SaveAndReimport();
        }
        return AssetDatabase.LoadAssetAtPath<Sprite>(CirclePath);
    }

    // Rounded rect with elliptical CapRadiusX × CapRadiusY corners, sliced so the ends stay rounded.
    static Sprite EnsureCapSprite()
    {
        int w = 2 * CapRadiusX + 2, h = 2 * CapRadiusY + 2;
        string signature = $"cap {CapRadiusX}x{CapRadiusY}";
        if (Stale(CapPath, signature))
        {
            WriteTexture(Supersample(w, h, (x, y) =>
            {
                // Inside the ellipse, measured outside the 2px flat middle.
                float dx = Mathf.Max(0f, Mathf.Abs(x - w / 2f) - 1f) / CapRadiusX;
                float dy = Mathf.Max(0f, Mathf.Abs(y - h / 2f) - 1f) / CapRadiusY;
                return dx * dx + dy * dy <= 1f;
            }), CapPath);
            ImportSprite(CapPath, new Vector4(CapRadiusX, CapRadiusY, CapRadiusX, CapRadiusY), signature);
        }
        return AssetDatabase.LoadAssetAtPath<Sprite>(CapPath);
    }

    // Thin circle outline, for the special-ready mark around player icons.
    static Sprite EnsureRingSprite()
    {
        const int size = 128;
        const string signature = "ring 0.86";
        if (Stale(RingPath, signature))
        {
            WriteTexture(Supersample(size, size, (x, y) =>
            {
                float d = Vector2.Distance(new Vector2(x, y), new Vector2(size / 2f, size / 2f)) / (size / 2f - 1f);
                return d <= 1f && d >= 0.86f;
            }), RingPath);
            ImportSprite(RingPath, Vector4.zero, signature);
        }
        return AssetDatabase.LoadAssetAtPath<Sprite>(RingPath);
    }

    // Upward-pointing triangle, for the local player's facing on the map.
    static Sprite EnsureArrowSprite()
    {
        const int size = 64;
        const string signature = "arrow";
        if (Stale(ArrowPath, signature))
        {
            WriteTexture(Supersample(size, size, (x, y) => y > 4f && Mathf.Abs(x - size / 2f) < (size - 4f - y) * 0.5f), ArrowPath);
            ImportSprite(ArrowPath, Vector4.zero, signature);
        }
        return AssetDatabase.LoadAssetAtPath<Sprite>(ArrowPath);
    }

    // Seamless tile of 45° anti-aliased white stripes (repeat-wrapped), for the turf bar.
    static Texture2D EnsureStripeTexture()
    {
        const int size = 128;
        string signature = $"stripes {StripesPerTile} 0.35";
        if (Stale(StripePath, signature))
        {
            WriteTexture(Supersample(size, size, (x, y) =>
            {
                float phase = (x - y) * StripesPerTile / size;
                return phase - Mathf.Floor(phase) < 0.35f; // 35% stripe, 65% gap
            }), StripePath);
            ImportTexture(StripePath, TextureWrapMode.Repeat, signature);
        }
        return AssetDatabase.LoadAssetAtPath<Texture2D>(StripePath);
    }

    // One period of a fill's edge: filled where x < width * (0.5 + 0.5 sin), repeating in v.
    static Texture2D EnsureWaveTexture()
    {
        const int w = 32, h = 64;
        const string signature = "wave sine";
        if (Stale(WavePath, signature))
        {
            WriteTexture(Supersample(w, h, (x, y) => x / w < 0.5f + 0.5f * Mathf.Sin(2f * Mathf.PI * y / h)), WavePath);
            ImportTexture(WavePath, TextureWrapMode.Clamp, signature);
        }
        return AssetDatabase.LoadAssetAtPath<Texture2D>(WavePath);
    }

    // Generated textures record their parameters in the importer and are rebuilt when they change.
    static bool Stale(string path, string signature)
    {
        AssetImporter importer = AssetImporter.GetAtPath(path);
        return importer == null || importer.userData != signature;
    }

    static void ImportSprite(string path, Vector4 border, string signature)
    {
        var importer = (TextureImporter)AssetImporter.GetAtPath(path);
        importer.textureType = TextureImporterType.Sprite;
        importer.spriteImportMode = SpriteImportMode.Single; // the project default here is Multiple, which yields no sprite
        importer.spriteBorder = border;
        importer.alphaIsTransparency = true;
        importer.mipmapEnabled = false;
        importer.userData = signature;
        importer.SaveAndReimport();
    }

    static void ImportTexture(string path, TextureWrapMode wrapU, string signature)
    {
        var importer = (TextureImporter)AssetImporter.GetAtPath(path);
        importer.textureType = TextureImporterType.Default;
        importer.wrapModeU = wrapU;
        importer.wrapModeV = TextureWrapMode.Repeat;
        importer.alphaIsTransparency = true;
        importer.mipmapEnabled = false;
        importer.userData = signature;
        importer.SaveAndReimport();
    }

    // White texture whose alpha is the 4×4 supersampled coverage of inside(x, y), in pixels.
    static Texture2D Supersample(int w, int h, System.Func<float, float, bool> inside)
    {
        const int samples = 4;
        var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            int hits = 0;
            for (int sy = 0; sy < samples; sy++)
            for (int sx = 0; sx < samples; sx++)
                if (inside(x + (sx + 0.5f) / samples, y + (sy + 0.5f) / samples)) hits++;
            tex.SetPixel(x, y, new Color(1f, 1f, 1f, hits / (float)(samples * samples)));
        }
        return tex;
    }

    static void WriteTexture(Texture2D tex, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllBytes(path, tex.EncodeToPNG());
        Object.DestroyImmediate(tex);
        AssetDatabase.ImportAsset(path);
    }
}

using System.IO;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

// UI building: the shared helpers the UI builders use (texts in the game's fonts, panels, buttons,
// generated sprites), and migrations that edit Assets/Prefabs/UI/GameUI.prefab in place (the volume
// sliders, the reticles). The HUD, menus and map were first generated here; GameUI is now the
// source of truth, so new UI goes in as a migration on it.
public static class MatchHUDBuilder
{
    const string PrefabPath = "Assets/Prefabs/UI/GameUI.prefab";
    const string CirclePath = "Assets/Art/UI/HudCircle.png";
    const string RingPath   = "Assets/Art/UI/HudRing.png";
    const string OutlineMat = "Assets/TextMesh Pro/Resources/Fonts & Materials/LiberationSans SDF - Outline.mat";

    static readonly Color PanelColour  = new Color(0.06f, 0.06f, 0.09f, 0.93f);
    static readonly Color ButtonColour = new Color(0.2f, 0.2f, 0.26f, 1f);

    static Sprite circle, rounded;
    static Material outline;

    // Sprites and materials the helpers below use; for other UI builders.
    internal static void LoadShared()
    {
        circle  = EnsureCircleSprite();
        rounded = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/UISprite.psd");
        outline = AssetDatabase.LoadAssetAtPath<Material>(OutlineMat);
    }
    internal static Sprite Rounded => rounded;

    // "Label   [====o----]   1.00x" across the panel.
    static Slider SettingRow(RectTransform panel, string label, float y, out TextMeshProUGUI value)
    {
        TextMeshProUGUI name = Text(label, panel, label, 20, Color.white, TextAlignmentOptions.Left, false, useOutline: false);
        Place(name.rectTransform, new Vector2(0.5f, 1f), new Vector2(0f, 0.5f), new Vector2(-330, y), new Vector2(300, 40));
        RectTransform sliderRt = Rect(label + " Slider", panel, new Vector2(0.5f, 1f), new Vector2(0.5f, 0.5f), new Vector2(110, y), new Vector2(240, 30));
        Slider slider = sliderRt.gameObject.AddComponent<Slider>();
        RectTransform track = Rect("Track", sliderRt, Vector2.zero, new Vector2(0.5f, 0.5f), Vector2.zero, Vector2.zero);
        track.anchorMin = new Vector2(0f, 0.35f); track.anchorMax = new Vector2(1f, 0.65f); track.sizeDelta = Vector2.zero;
        Img(track.gameObject, rounded, new Color(0f, 0f, 0f, 0.45f), sliced: true);
        RectTransform fillArea = Rect("Fill Area", sliderRt, Vector2.zero, new Vector2(0.5f, 0.5f), Vector2.zero, Vector2.zero);
        fillArea.anchorMin = new Vector2(0f, 0.35f); fillArea.anchorMax = new Vector2(1f, 0.65f); fillArea.sizeDelta = Vector2.zero;
        RectTransform fill = Rect("Fill", fillArea, Vector2.zero, new Vector2(0.5f, 0.5f), Vector2.zero, Vector2.zero);
        fill.sizeDelta = Vector2.zero;
        Img(fill.gameObject, rounded, new Color(0.96f, 0.78f, 0.25f), sliced: true);
        RectTransform handleArea = Rect("Handle Slide Area", sliderRt, Vector2.zero, new Vector2(0.5f, 0.5f), Vector2.zero, Vector2.zero);
        Stretch(handleArea);
        RectTransform handle = Rect("Handle", handleArea, Vector2.zero, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(28, 0));
        handle.anchorMin = new Vector2(0f, 0f); handle.anchorMax = new Vector2(0f, 1f);
        Image knob = Img(handle.gameObject, circle, Color.white, sliced: false);
        knob.raycastTarget = true;
        slider.fillRect = fill;
        slider.handleRect = handle;
        slider.targetGraphic = knob;
        slider.direction = Slider.Direction.LeftToRight;
        value = Text("Value", panel, "1.00×", 20, Color.white, TextAlignmentOptions.Right, true, useOutline: false);
        Place(value.rectTransform, new Vector2(0.5f, 1f), new Vector2(1f, 0.5f), new Vector2(330, y), new Vector2(100, 40));
        return slider;
    }

    internal static RectTransform Panel(string name, RectTransform parent, Vector2 size)
    {
        RectTransform panel = Rect(name, parent, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, size);
        Img(panel.gameObject, rounded, PanelColour, sliced: true).raycastTarget = true; // swallow clicks behind it
        return panel;
    }

    internal static void Title(RectTransform panel, string text)
    {
        TextMeshProUGUI title = Text("Title", panel, text, 34, Color.white, TextAlignmentOptions.Center, true);
        Place(title.rectTransform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0, -18), new Vector2(600, 44));
    }

    internal static Button MenuButton(string name, RectTransform parent, string text, Vector2 pos, Vector2 size, out TextMeshProUGUI label)
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

    // ── Helpers ────────────────────────────────────────────────────────────

    internal static RectTransform Rect(string name, Transform parent, Vector2 anchor, Vector2 pivot, Vector2 pos, Vector2 size)
    {
        RectTransform rt = new GameObject(name, typeof(RectTransform)).GetComponent<RectTransform>();
        rt.SetParent(parent, false);
        Place(rt, anchor, pivot, pos, size);
        return rt;
    }

    internal static void Place(RectTransform rt, Vector2 anchor, Vector2 pivot, Vector2 pos, Vector2 size)
    {
        rt.anchorMin = rt.anchorMax = anchor;
        rt.pivot = pivot;
        rt.anchoredPosition = pos;
        rt.sizeDelta = size;
    }

    internal static void Stretch(RectTransform rt) => StretchInset(rt, 0);

    internal static void StretchInset(RectTransform rt, float inset)
    {
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = new Vector2(inset, inset);
        rt.offsetMax = new Vector2(-inset, -inset);
    }

    internal static Image Img(GameObject go, Sprite sprite, Color colour, bool sliced)
    {
        Image img = go.AddComponent<Image>();
        img.sprite = sprite;
        img.type = sliced ? Image.Type.Sliced : Image.Type.Simple;
        img.color = colour;
        img.raycastTarget = false;
        return img;
    }

    // Shows the controls named in the template ({Map/Action}) as icons for the current input.
    internal static void Prompt(TMP_Text text, string template)
    {
        var so = new SerializedObject(text.gameObject.AddComponent<ControlPrompt>());
        so.FindProperty("template").stringValue = template;
        so.ApplyModifiedPropertiesWithoutUndo();
    }

    internal static TextMeshProUGUI Text(string name, Transform parent, string text, float size, Color colour,
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
        TMP_FontAsset font = bold ? UIFonts.Heading : UIFonts.Detail; // Super Waffles for headings, Kronika for detail
        if (font != null)
        {
            t.font = font;
            t.fontSharedMaterial = useOutline ? (bold ? UIFonts.HeadingOutline : UIFonts.DetailOutline) : font.material; // outlined: readable over bright scenes
        }
        else if (useOutline && outline != null) t.fontSharedMaterial = outline;
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

    static void DestroyChild(Transform parent, string name)
    {
        Transform old = parent.Find(name);
        if (old != null) Object.DestroyImmediate(old.gameObject);
    }

    static TMP_Text Label(Transform parent, string source, string name, float ax, float ay)
    {
        DestroyChild(parent, name);
        var src = (RectTransform)parent.Find(source);
        var label = (RectTransform)Object.Instantiate(src, parent);
        label.name = name;
        label.anchorMin = label.anchorMax = new Vector2(ax, ay);
        label.pivot = new Vector2(0.5f, src.pivot.y);
        label.anchoredPosition = new Vector2(0f, src.anchoredPosition.y);
        TMP_Text text = label.GetComponent<TMP_Text>();
        text.alignment = TextAlignmentOptions.Center;
        label.gameObject.SetActive(false);
        return text;
    }

    // ── Migrations on GameUI ───────────────────────────────────────────────

    // Music and effects volume sliders, under the gyro toggle in the settings panel (in place:
    // run again, they're rebuilt).
    [MenuItem("Tools/UI/Add Volume Settings To Pause Menu")]
    public static void AddVolumeSettings()
    {
        circle  = EnsureCircleSprite();
        rounded = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/UISprite.psd");
        GameObject root = PrefabUtility.LoadPrefabContents(PrefabPath);
        try
        {
            PauseMenu menu = root.GetComponentInChildren<PauseMenu>(true);
            var so = new SerializedObject(menu);
            var panel = (RectTransform)((GameObject)so.FindProperty("settingsPanel").objectReferenceValue).transform;
            foreach (string label in new[] { MusicLabel, SfxLabel })
                foreach (string part in new[] { label, label + " Slider", label + " Value" })
                    DestroyChild(panel, part);
            panel.sizeDelta = new Vector2(panel.sizeDelta.x, 700f);
            Slider music = SettingRow(panel, MusicLabel, -450, out TextMeshProUGUI musicValue);
            Slider sfx = SettingRow(panel, SfxLabel, -520, out TextMeshProUGUI sfxValue);
            musicValue.name = MusicLabel + " Value";
            sfxValue.name = SfxLabel + " Value";
            musicValue.text = "85%";
            sfxValue.text = "100%";
            var back = (Button)so.FindProperty("backButton").objectReferenceValue;
            ((RectTransform)back.transform).anchoredPosition = new Vector2(0, -600);
            so.FindProperty("musicSlider").objectReferenceValue = music;
            so.FindProperty("sfxSlider").objectReferenceValue = sfx;
            so.FindProperty("musicValue").objectReferenceValue = musicValue;
            so.FindProperty("sfxValue").objectReferenceValue = sfxValue;
            so.ApplyModifiedPropertiesWithoutUndo();
            PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }
        Debug.Log($"[MatchHUD] Volume settings in the pause menu: {PrefabPath}");
    }

    const string MusicLabel = "Music volume", SfxLabel = "Effects volume";

    // The aim marks (Reticle): the aligned grey ring and the true mark (a thicker white ring round a dot), under
    // everything else on the HUD. In place: run again, it's rebuilt.
    [MenuItem("Tools/UI/Add Reticles To Match HUD")]
    public static void AddReticles()
    {
        circle = EnsureCircleSprite();
        Sprite ring = EnsureRingSprite(), thick = EnsureThickRingSprite();
        GameObject root = PrefabUtility.LoadPrefabContents(PrefabPath);
        try
        {
            Transform live = root.GetComponent<GameUI>() != null ? root.transform.Find("Live") : root.transform; // GameUI's nested canvas for things that move each frame
            DestroyChild(live, "Reticles");
            Vector2 centre = new Vector2(0.5f, 0.5f);
            RectTransform area = Rect("Reticles", live, centre, centre, Vector2.zero, Vector2.zero);
            Stretch(area);
            area.SetSiblingIndex(0); // under the ink tank (and the cursor)
            CanvasGroup group = area.gameObject.AddComponent<CanvasGroup>();
            group.blocksRaycasts = false;
            group.interactable = false;

            RectTransform aligned = Rect("Aligned", area, centre, centre, Vector2.zero, new Vector2(46, 46));
            Img(aligned.gameObject, ring, new Color(0.78f, 0.78f, 0.8f, 0.7f), sliced: false);

            RectTransform mark = Rect("True", area, centre, centre, Vector2.zero, new Vector2(46, 46)); // the aligned ring's size: covers it when lined up
            Color white = Color.white, shade = new Color(0f, 0f, 0f, 0.6f);
            Shade(Img(mark.gameObject, thick, white, sliced: false), shade);
            RectTransform dot = Rect("Dot", mark, centre, centre, Vector2.zero, new Vector2(5, 5));
            Shade(Img(dot.gameObject, circle, white, sliced: false), shade);

            Reticle reticle = area.gameObject.AddComponent<Reticle>();
            var so = new SerializedObject(reticle);
            so.FindProperty("aligned").objectReferenceValue = aligned;
            so.FindProperty("trueMark").objectReferenceValue = mark;
            so.FindProperty("group").objectReferenceValue = group;
            so.ApplyModifiedPropertiesWithoutUndo();
            PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }
        Debug.Log($"[MatchHUD] Reticles on {PrefabPath}");
    }

    // A ring with a wider band than HudRing (22% of its radius, against 14%), for the true reticle.
    static Sprite EnsureThickRingSprite()
    {
        const int size = 128;
        const string path = "Assets/Art/UI/HudRingThick.png", signature = "ring 0.78";
        if (Stale(path, signature))
        {
            WriteTexture(Supersample(size, size, (x, y) =>
            {
                float d = Vector2.Distance(new Vector2(x, y), new Vector2(size / 2f, size / 2f)) / (size / 2f - 1f);
                return d <= 1f && d >= 0.78f;
            }), path);
            ImportSprite(path, Vector4.zero, signature);
        }
        return AssetDatabase.LoadAssetAtPath<Sprite>(path);
    }

    static void Shade(Image image, Color colour)
    {
        image.raycastTarget = false;
        Outline o = image.gameObject.AddComponent<Outline>();
        o.effectColor = colour;
        o.effectDistance = new Vector2(1.5f, -1.5f);
    }
}

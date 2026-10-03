using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using TMPro;
using UnityEditor;
using UnityEngine;

// Builds the control icons from Assets/Textures/buttonsheet.png (sliced, sprites named
// buttonsheet_<name>): a TMP sprite asset beside it, so text can show them inline, and
// Assets/Resources/ControlGlyphs.asset mapping each control to its sprite. Rebuild after adding
// sprites to the sheet; controls with no sprite show as text. The d-pad's left/right/down icons are
// DpadSelected (up) rotated, generated into buttonsheet_dpad.png with its own sprite asset, which
// the sheet's falls back to (TMP can't rotate a sprite in text).
public static class ControlGlyphsBuilder
{
    const string SheetPath = "Assets/Textures/buttonsheet.png";
    const string SpriteAssetPath = "Assets/Textures/buttonsheet.asset";
    const string GlyphsPath = "Assets/Resources/ControlGlyphs.asset";
    const string DpadSheetPath = "Assets/Textures/buttonsheet_dpad.png";
    const string DpadSpriteAssetPath = "Assets/Textures/buttonsheet_dpad.asset";
    const string Prefix = "buttonsheet_";
    const float SheetCell = 48f;      // the sheet's key height in pixels
    const float IconToFont = 1.2f;    // an icon's height in text, as a share of the font size
    const float AboveBaseline = 0.8f; // and how much of it sits above the baseline (the rest hangs below, like the text's descenders)

    // Control → sprite name, for every gamepad family.
    static readonly (string control, string sprite)[] Shared =
    {
        ("Keyboard/space", "space"), ("Keyboard/w", "W"), ("Keyboard/a", "A"), ("Keyboard/s", "S"), ("Keyboard/d", "D"),
        ("Keyboard/e", "E"), ("Keyboard/f", "F"), ("Keyboard/v", "V"), ("Keyboard/r", "R"), ("Keyboard/q", "Q"),
        ("Keyboard/escape", "Esc"), ("Keyboard/leftShift", "Shift"), ("Keyboard/rightShift", "Shift"), ("Keyboard/shift", "Shift"),
        ("Keyboard/enter", "Enter"), ("Keyboard/numpadEnter", "Enter"),
        ("Keyboard/tab", "Tab"), ("Keyboard/l", "L"), ("Keyboard/g", "G"),
        // The sheet's names are the other way round: its "Select" art is the three-line Menu button (Start)
        // and its "Start" art the two-square View button (Select).
        ("Gamepad/start", "controllerSelect"), ("Gamepad/select", "controllerStart"),
        ("Mouse/leftButton", "MouseL"), ("Mouse/rightButton", "MouseR"), ("Mouse/middleButton", "Mouse3"),
        ("Mouse/delta", "Mouse"), ("Mouse/position", "Mouse"), ("Mouse/scroll", "Mouse3"),
        ("Gamepad/leftStick", "controllerLS"), ("Gamepad/leftStickPress", "controllerLS"),
        ("Gamepad/rightStick", "controllerRS"), ("Gamepad/rightStickPress", "controllerRS"),
        ("Gamepad/dpad", "controllerDpad"), ("Gamepad/dpad/up", "controllerDpadSelected"),
        ("Gamepad/leftShoulder", "controllerLB"), ("Gamepad/rightShoulder", "controllerRB"),
        ("Gamepad/leftTrigger", "controllerLT"), ("Gamepad/rightTrigger", "controllerRT"),
    };

    // Used when the sheet has them (otherwise these fall back to the plain d-pad).
    static readonly (string control, string sprite)[] Optional =
    {
        ("Gamepad/dpad/left", "controllerDpadLeft"), ("Gamepad/dpad/right", "controllerDpadRight"), ("Gamepad/dpad/down", "controllerDpadDown"),
    };

    // Face buttons by position: south, east, west, north.
    static readonly (PadFamily family, string[] faces)[] Faces =
    {
        (PadFamily.Xbox,        new[] { "controllerA", "controllerB", "controllerX", "controllerY" }),
        (PadFamily.PlayStation, new[] { "controllerPSX", "controllerPSCircle", "controllerPSSquare", "controllerPSTriangle" }),
        (PadFamily.Nintendo,    new[] { "controllerB", "controllerA", "controllerY", "controllerX" }), // A on the right
    };
    static readonly string[] FaceControls = { "Gamepad/buttonSouth", "Gamepad/buttonEast", "Gamepad/buttonWest", "Gamepad/buttonNorth" };

    [MenuItem("Tools/UI/Build Control Glyphs")]
    public static void Build()
    {
        Texture2D sheet = AssetDatabase.LoadAssetAtPath<Texture2D>(SheetPath);
        Dictionary<string, Sprite> sprites = SpritesIn(SheetPath);
        bool dpad = BuildDpadDirections(sprites);
        if (dpad) foreach (var pair in SpritesIn(DpadSheetPath)) sprites[pair.Key] = pair.Value;

        TMP_SpriteAsset spriteAsset = MakeSpriteAsset(sheet, SpriteAssetPath);
        if (dpad)
        {
            TMP_SpriteAsset dpadAsset = MakeSpriteAsset(AssetDatabase.LoadAssetAtPath<Texture2D>(DpadSheetPath), DpadSpriteAssetPath);
            spriteAsset.fallbackSpriteAssets = new List<TMP_SpriteAsset> { dpadAsset }; // <sprite name=...> looks there too
            EditorUtility.SetDirty(spriteAsset);
        }

        var glyphs = new List<ControlGlyphs.Glyph>();
        var missing = new List<string>();
        void Add(string control, string name, bool anyFamily, PadFamily family)
        {
            if (sprites.TryGetValue(name, out Sprite sprite)) glyphs.Add(new ControlGlyphs.Glyph { control = control, sprite = sprite, anyFamily = anyFamily, family = family });
            else missing.Add(name);
        }
        foreach (var (control, sprite) in Shared) Add(control, sprite, true, PadFamily.Xbox);
        foreach (var (control, sprite) in Optional)
            if (sprites.ContainsKey(sprite)) Add(control, sprite, true, PadFamily.Xbox);
        foreach (var (family, faces) in Faces)
            for (int i = 0; i < FaceControls.Length; i++) Add(FaceControls[i], faces[i], false, family);

        if (!AssetDatabase.IsValidFolder("Assets/Resources")) AssetDatabase.CreateFolder("Assets", "Resources");
        ControlGlyphs asset = AssetDatabase.LoadAssetAtPath<ControlGlyphs>(GlyphsPath);
        if (asset == null) { asset = ScriptableObject.CreateInstance<ControlGlyphs>(); AssetDatabase.CreateAsset(asset, GlyphsPath); }
        var so = new SerializedObject(asset);
        so.FindProperty("spriteAsset").objectReferenceValue = spriteAsset;
        SerializedProperty list = so.FindProperty("glyphs");
        list.arraySize = glyphs.Count;
        for (int i = 0; i < glyphs.Count; i++)
        {
            SerializedProperty e = list.GetArrayElementAtIndex(i);
            e.FindPropertyRelative("control").stringValue = glyphs[i].control;
            e.FindPropertyRelative("anyFamily").boolValue = glyphs[i].anyFamily;
            e.FindPropertyRelative("family").enumValueIndex = (int)glyphs[i].family;
            e.FindPropertyRelative("sprite").objectReferenceValue = glyphs[i].sprite;
        }
        so.ApplyModifiedPropertiesWithoutUndo();
        AssetDatabase.SaveAssets();
        Debug.Log($"[ControlGlyphs] {glyphs.Count} glyphs from {sprites.Count} sprites" + (missing.Count > 0 ? $"; no sprite for {string.Join(", ", missing.Distinct())}" : ""));
    }

    static Dictionary<string, Sprite> SpritesIn(string path) =>
        AssetDatabase.LoadAllAssetsAtPath(path).OfType<Sprite>()
            .ToDictionary(s => s.name.StartsWith(Prefix) ? s.name.Substring(Prefix.Length) : s.name);

    // A TMP sprite asset for a sliced texture, made the way TMP's own "Create > Sprite Asset" menu
    // does, then sized to the text (a key cell is IconToFont x the font size) with each icon set like
    // a character: starting at the cursor, mostly above the baseline.
    static TMP_SpriteAsset MakeSpriteAsset(Texture2D texture, string path)
    {
        if (AssetDatabase.LoadAssetAtPath<TMP_SpriteAsset>(path) != null) AssetDatabase.DeleteAsset(path); // rebuilt from the current slices
        System.Type menu = typeof(TMP_SpriteAsset).Assembly.GetType("TMPro.EditorUtilities.TMP_SpriteAssetMenu")
                        ?? System.AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("TMPro.EditorUtilities.TMP_SpriteAssetMenu")).FirstOrDefault(t => t != null);
        menu.GetMethod("CreateSpriteAssetFromSelectedObject", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, new object[] { texture });
        TMP_SpriteAsset spriteAsset = AssetDatabase.LoadAssetAtPath<TMP_SpriteAsset>(path);

        var face = spriteAsset.faceInfo;
        face.pointSize = Mathf.RoundToInt(SheetCell / IconToFont);
        face.scale = 1f;
        face.lineHeight = SheetCell;
        face.ascentLine = SheetCell * AboveBaseline;
        face.descentLine = -SheetCell * (1f - AboveBaseline);
        spriteAsset.faceInfo = face;
        foreach (TMP_SpriteGlyph glyph in spriteAsset.spriteGlyphTable)
        {
            var m = glyph.metrics;
            glyph.metrics = new UnityEngine.TextCore.GlyphMetrics(m.width, m.height, 0f, m.height * AboveBaseline, m.width);
        }
        spriteAsset.UpdateLookupTables();
        EditorUtility.SetDirty(spriteAsset);
        return spriteAsset;
    }

    // DpadSelected (up lit) turned to light left, right and down, side by side in buttonsheet_dpad.png,
    // sliced as buttonsheet_controllerDpadLeft/Right/Down. False if the sheet has no DpadSelected.
    static bool BuildDpadDirections(Dictionary<string, Sprite> sprites)
    {
        if (!sprites.TryGetValue("controllerDpadSelected", out Sprite up)) return false;
        var source = new Texture2D(2, 2);
        source.LoadImage(System.IO.File.ReadAllBytes(SheetPath)); // a readable copy (the sheet itself isn't)
        RectInt r = new RectInt((int)up.rect.x, (int)up.rect.y, (int)up.rect.width, (int)up.rect.height);
        int n = Mathf.Min(r.width, r.height);
        Color32[] src = source.GetPixels32();
        Color32 At(int x, int y) => src[(r.y + y) * source.width + r.x + x];

        // Source pixel for each destination pixel, per turn: left (a quarter turn anticlockwise),
        // right (clockwise), down (half).
        var turns = new (string name, System.Func<int, int, Vector2Int> from)[]
        {
            ("controllerDpadLeft",  (x, y) => new Vector2Int(y, n - 1 - x)),
            ("controllerDpadRight", (x, y) => new Vector2Int(n - 1 - y, x)),
            ("controllerDpadDown",  (x, y) => new Vector2Int(n - 1 - x, n - 1 - y)),
        };
        var strip = new Texture2D(n * turns.Length, n, TextureFormat.RGBA32, false);
        var pixels = new Color32[strip.width * n];
        for (int t = 0; t < turns.Length; t++)
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    Vector2Int s = turns[t].from(x, y);
                    pixels[y * strip.width + t * n + x] = At(s.x, s.y);
                }
        strip.SetPixels32(pixels);
        System.IO.File.WriteAllBytes(DpadSheetPath, strip.EncodeToPNG());
        Object.DestroyImmediate(strip);
        Object.DestroyImmediate(source);
        AssetDatabase.ImportAsset(DpadSheetPath);

        // Imported like the sheet, sliced into the three.
        var sheetImporter = (TextureImporter)AssetImporter.GetAtPath(SheetPath);
        var importer = (TextureImporter)AssetImporter.GetAtPath(DpadSheetPath);
        importer.textureType = TextureImporterType.Sprite;
        importer.spriteImportMode = SpriteImportMode.Multiple;
        importer.alphaIsTransparency = true;
        importer.mipmapEnabled = sheetImporter.mipmapEnabled;
        importer.filterMode = sheetImporter.filterMode;
        importer.textureCompression = sheetImporter.textureCompression;
#pragma warning disable CS0618 // spritesheet: still the plain way to slice from code
        importer.spritesheet = turns.Select((turn, i) => new SpriteMetaData
        {
            name = Prefix + turn.name, rect = new Rect(i * n, 0, n, n), alignment = (int)SpriteAlignment.Center, pivot = new Vector2(0.5f, 0.5f),
        }).ToArray();
#pragma warning restore CS0618
        importer.SaveAndReimport();
        return true;
    }
}

using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using TMPro;
using UnityEditor;
using UnityEngine;

// Builds the control icons from Assets/Textures/buttonsheet.png (sliced, sprites named
// buttonsheet_<name>): a TMP sprite asset beside it, so text can show them inline, and
// Assets/Resources/ControlGlyphs.asset mapping each control to its sprite. Rebuild after adding
// sprites to the sheet; controls with no sprite show as text.
public static class ControlGlyphsBuilder
{
    const string SheetPath = "Assets/Textures/buttonsheet.png";
    const string SpriteAssetPath = "Assets/Textures/buttonsheet.asset";
    const string GlyphsPath = "Assets/Resources/ControlGlyphs.asset";
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
        Dictionary<string, Sprite> sprites = AssetDatabase.LoadAllAssetsAtPath(SheetPath).OfType<Sprite>()
            .ToDictionary(s => s.name.StartsWith(Prefix) ? s.name.Substring(Prefix.Length) : s.name);

        // The TMP sprite asset, made the way TMP's own "Create > Sprite Asset" menu does.
        TMP_SpriteAsset spriteAsset = AssetDatabase.LoadAssetAtPath<TMP_SpriteAsset>(SpriteAssetPath);
        if (spriteAsset != null) AssetDatabase.DeleteAsset(SpriteAssetPath); // rebuilt from the current slices
        System.Type menu = typeof(TMP_SpriteAsset).Assembly.GetType("TMPro.EditorUtilities.TMP_SpriteAssetMenu")
                        ?? System.AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("TMPro.EditorUtilities.TMP_SpriteAssetMenu")).FirstOrDefault(t => t != null);
        menu.GetMethod("CreateSpriteAssetFromSelectedObject", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, new object[] { sheet });
        spriteAsset = AssetDatabase.LoadAssetAtPath<TMP_SpriteAsset>(SpriteAssetPath);

        // Size icons to the text (a key cell is IconToFont x the font size) and set each one like a
        // character: starting at the cursor, mostly above the baseline.
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
}

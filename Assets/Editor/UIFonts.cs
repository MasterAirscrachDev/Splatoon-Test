using System.Collections.Generic;
using System.IO;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

// The game's two typefaces: Super Waffles for names, headers and titles (anything bold, and the
// texts that show a name); Kronika for the detail (descriptions, notes, hints, settings, the feed).
// Their TMP font assets are dynamic (any character a Steam name has gets added as it's met) with
// Liberation Sans behind them for glyphs they don't have. Each has an outlined preset like the one
// the HUD used. Tools/UI/Apply Fonts makes them (once) and sets every UI prefab's texts, and the
// lobby's kiosk labels, by that rule; MatchHUDBuilder.Text builds new texts the same way.
public static class UIFonts
{
    const string Dir = "Assets/Fonts";
    const string HeadingTtf = Dir + "/Super Waffles.ttf", DetailTtf = Dir + "/KRONIKA.ttf";
    const string HeadingAsset = Dir + "/Super Waffles SDF.asset", DetailAsset = Dir + "/Kronika SDF.asset";
    const string LiberationAsset = "Assets/TextMesh Pro/Resources/Fonts & Materials/LiberationSans SDF.asset";
    const string LiberationOutline = "Assets/TextMesh Pro/Resources/Fonts & Materials/LiberationSans SDF - Outline.mat";
    const string Characters = " !\"#$%&'()*+,-./0123456789:;<=>?@ABCDEFGHIJKLMNOPQRSTUVWXYZ[\\]^_`abcdefghijklmnopqrstuvwxyz{|}~×—–…·•°";

    // Shown with a name, though not bold.
    static readonly HashSet<string> NameTexts = new HashSet<string> { "Name", "Killer", "Host", "WeaponName", "MarkerTemplate", "Title" };

    public static TMP_FontAsset Heading => Font(HeadingTtf, HeadingAsset);
    public static TMP_FontAsset Detail => Font(DetailTtf, DetailAsset);
    public static Material HeadingOutline => Outline(Heading);
    public static Material DetailOutline => Outline(Detail);

    public static bool IsHeading(TMP_Text t) => (t.fontStyle & FontStyles.Bold) != 0 || NameTexts.Contains(t.name) || t.name.StartsWith("Header");

    // Sets a text's typeface by its role, keeping whether it was outlined.
    public static void Apply(TMP_Text t)
    {
        bool heading = IsHeading(t);
        bool outlined = t.fontSharedMaterial != null && t.fontSharedMaterial.name.Contains("Outline");
        TMP_FontAsset font = heading ? Heading : Detail;
        if (font == null) return;
        t.font = font;
        t.fontSharedMaterial = outlined ? Outline(font) : font.material;
        EditorUtility.SetDirty(t);
    }

    [MenuItem("Tools/UI/Apply Fonts")]
    public static void ApplyMenu() => Debug.Log(ApplyAll());

    public static string ApplyAll()
    {
        var log = new List<string>();
        int texts = 0;
        foreach (string guid in AssetDatabase.FindAssets("t:Prefab", new[] { "Assets/Prefabs" }))
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            if (AssetDatabase.LoadAssetAtPath<GameObject>(path).GetComponentInChildren<TMP_Text>(true) == null) continue;
            GameObject root = PrefabUtility.LoadPrefabContents(path);
            try
            {
                foreach (TMP_Text t in root.GetComponentsInChildren<TMP_Text>(true)) { Apply(t); texts++; }
                PrefabUtility.SaveAsPrefabAsset(root, path);
                log.Add(Path.GetFileNameWithoutExtension(path));
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }
        log.Add($"{texts} prefab texts");
        log.Add(ApplyToKiosks());
        SetDefault();
        MakeGameFonts();
        AssetDatabase.SaveAssets();
        return "[UIFonts] " + string.Join("; ", log);
    }

    // The lobby's kiosk signs: their titles (Super Waffles).
    static string ApplyToKiosks()
    {
        const string Lobby = "Assets/Scenes/Lobby.unity";
        var scene = UnityEngine.SceneManagement.SceneManager.GetSceneByPath(Lobby);
        bool opened = !scene.isLoaded;
        if (opened) scene = EditorSceneManager.OpenScene(Lobby, OpenSceneMode.Additive);
        int n = 0;
        try
        {
            foreach (GameObject root in scene.GetRootGameObjects())
                foreach (LobbyKiosk kiosk in root.GetComponentsInChildren<LobbyKiosk>(true))
                    foreach (TMP_Text t in kiosk.GetComponentsInChildren<TMP_Text>(true))
                    {
                        t.font = Heading;
                        t.fontSharedMaterial = Heading.material;
                        EditorUtility.SetDirty(t);
                        n++;
                    }
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
        }
        finally
        {
            if (opened) EditorSceneManager.CloseScene(scene, true);
        }
        return $"{n} kiosk labels";
    }

    // Resources/GameFonts: the faces for texts made in code (the target dummies' labels, say).
    static void MakeGameFonts()
    {
        const string path = "Assets/Resources/GameFonts.asset";
        var set = AssetDatabase.LoadAssetAtPath<GameFonts>(path);
        if (set == null) { set = ScriptableObject.CreateInstance<GameFonts>(); AssetDatabase.CreateAsset(set, path); }
        set.heading = Heading; set.detail = Detail;
        set.headingOutline = HeadingOutline; set.detailOutline = DetailOutline;
        EditorUtility.SetDirty(set);
    }

    // Texts made in code without a font get Kronika.
    static void SetDefault()
    {
        TMP_Settings settings = Resources.Load<TMP_Settings>("TMP Settings");
        if (settings == null) return;
        var so = new SerializedObject(settings);
        so.FindProperty("m_defaultFontAsset").objectReferenceValue = Detail;
        so.ApplyModifiedPropertiesWithoutUndo();
        EditorUtility.SetDirty(settings);
    }

    static TMP_FontAsset Font(string ttf, string assetPath)
    {
        var existing = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(assetPath);
        if (existing != null) return existing;
        var source = AssetDatabase.LoadAssetAtPath<Font>(ttf);
        if (source == null) { Debug.LogError($"[UIFonts] no font at {ttf}"); return null; }
        TMP_FontAsset font = TMP_FontAsset.CreateFontAsset(source, 90, 9, UnityEngine.TextCore.LowLevel.GlyphRenderMode.SDFAA, 1024, 1024, AtlasPopulationMode.Dynamic, true);
        font.name = Path.GetFileNameWithoutExtension(assetPath);
        AssetDatabase.CreateAsset(font, assetPath);
        font.atlasTextures[0].name = font.name + " Atlas";
        AssetDatabase.AddObjectToAsset(font.atlasTextures[0], font);
        font.material.name = font.name + " Material";
        AssetDatabase.AddObjectToAsset(font.material, font);
        font.TryAddCharacters(Characters, out string missing);
        var liberation = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(LiberationAsset);
        if (liberation != null) font.fallbackFontAssetTable = new List<TMP_FontAsset> { liberation };
        EditorUtility.SetDirty(font);
        AssetDatabase.SaveAssets();
        if (!string.IsNullOrEmpty(missing)) Debug.Log($"[UIFonts] {font.name} hasn't got \"{missing}\" (Liberation Sans stands in)");
        return font;
    }

    // An outlined preset for the font, like Liberation Sans' that the HUD used.
    static Material Outline(TMP_FontAsset font)
    {
        if (font == null) return null;
        string path = $"{Dir}/{font.name} - Outline.mat";
        var existing = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (existing != null) return existing;
        var basis = AssetDatabase.LoadAssetAtPath<Material>(LiberationOutline);
        var mat = new Material(font.material) { name = font.name + " - Outline" };
        if (basis != null)
        {
            mat.shader = basis.shader;
            foreach (string keyword in basis.shaderKeywords) mat.EnableKeyword(keyword);
            foreach (string prop in new[] { "_OutlineWidth", "_OutlineSoftness", "_FaceDilate", "_UnderlayOffsetX", "_UnderlayOffsetY", "_UnderlayDilate", "_UnderlaySoftness" })
                if (basis.HasProperty(prop)) mat.SetFloat(prop, basis.GetFloat(prop));
            foreach (string prop in new[] { "_OutlineColor", "_UnderlayColor", "_FaceColor" })
                if (basis.HasProperty(prop)) mat.SetColor(prop, basis.GetColor(prop));
        }
        mat.SetTexture(ShaderUtilities.ID_MainTex, font.atlasTexture);
        AssetDatabase.CreateAsset(mat, path);
        return mat;
    }
}

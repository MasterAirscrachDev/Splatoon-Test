using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

// Renders the icon studio's entries (see IconStudio) to transparent PNG sprites and sets each as its
// prefab's icon (Weapon.Icon, SubDevice.Icon). Rendering happens in an isolated preview scene with
// copies of the studio's camera and lights, so whatever else is open doesn't get in the shot.
public static class IconStudioRenderer
{
    public const string ScenePath = "Assets/Scenes/IconStudio.unity";

    // ── Scene (create-only) ────────────────────────────────────────────────

    [MenuItem("Tools/Icons/Build Icon Studio Scene")]
    public static void BuildScene()
    {
        if (File.Exists(ScenePath)) { Debug.Log($"[Icons] {ScenePath} already exists; open it to tweak the shots"); return; }
        Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
        try
        {
            var studioGo = new GameObject("IconStudio");
            SceneManager.MoveGameObjectToScene(studioGo, scene);
            IconStudio studio = studioGo.AddComponent<IconStudio>();

            var camGo = new GameObject("IconCamera");
            camGo.transform.SetParent(studioGo.transform, false);
            camGo.transform.SetPositionAndRotation(new Vector3(0f, 0f, -20f), Quaternion.identity);
            Camera cam = camGo.AddComponent<Camera>();
            cam.orthographic = true;
            cam.orthographicSize = 1f;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0f, 0f, 0f, 0f);
            cam.nearClipPlane = 0.1f;
            cam.farClipPlane = 100f;
            cam.enabled = false; // only renders on request
            studio.cam = cam;

            // Key from the upper left front, a cool fill from the right, a rim from behind.
            studio.lights = new[]
            {
                StudioLight(studioGo.transform, "Key",  new Vector3(35f, 30f, 0f),    new Color(1f, 0.97f, 0.92f), 1.15f),
                StudioLight(studioGo.transform, "Fill", new Vector3(10f, -50f, 0f),   new Color(0.8f, 0.87f, 1f), 0.55f),
                StudioLight(studioGo.transform, "Rim",  new Vector3(20f, 160f, 0f),   Color.white, 0.7f),
            };
            Gather(studio);
            EditorSceneManager.SaveScene(scene, ScenePath);
            Debug.Log($"[Icons] Built {ScenePath} with {studio.entries.Count} entries");
        }
        finally
        {
            EditorSceneManager.CloseScene(scene, true);
        }
    }

    static Light StudioLight(Transform parent, string name, Vector3 euler, Color colour, float intensity)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        go.transform.rotation = Quaternion.Euler(euler);
        Light l = go.AddComponent<Light>();
        l.type = LightType.Directional;
        l.color = colour;
        l.intensity = intensity;
        l.shadows = LightShadows.None;
        return l;
    }

    // Adds an entry for every weapon and sub prefab that hasn't got one: weapons pointing right,
    // rollers drum up with the handle hanging down, subs stood up and seen a little from above.
    public static readonly Vector3 WeaponAngle = new Vector3(10f, 110f, 0f), RollerAngle = new Vector3(-65f, 25f, 15f), SubAngle = new Vector3(-25f, 30f, 0f);
    public static int Gather(IconStudio studio)
    {
        int added = 0;
        foreach (string guid in AssetDatabase.FindAssets("t:Prefab", new[] { "Assets/Prefabs" }))
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GUIDToAssetPath(guid));
            bool weapon = prefab.TryGetComponent(out Weapon _), sub = prefab.TryGetComponent(out SubDevice _);
            if (!weapon && !sub || studio.entries.Any(e => e.prefab == prefab && !e.special)) continue;
            studio.entries.Add(new IconStudio.Entry { prefab = prefab, angle = prefab.TryGetComponent(out Roller _) ? RollerAngle : weapon ? WeaponAngle : SubAngle });
            added++;
        }
        // Specials: the Inkstrike's ink column (without its target ring); a kid in the bubble shield.
        GameObject player = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/PlayerEntity - VOID.prefab");
        GameObject strike = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/InkStrike.prefab");
        if (player != null && !studio.entries.Any(x => x.special && x.specialType == SpecialType.BubbleShield))
        {
            studio.entries.Add(new IconStudio.Entry { prefab = player, angle = new Vector3(-10f, 200f, 0f), special = true, specialType = SpecialType.BubbleShield,
                                                      show = new[] { "BubbleShield" }, inkParts = new[] { "BubbleShield" }, fileName = "BubbleShield" });
            added++;
        }
        if (strike != null && !studio.entries.Any(x => x.special && x.specialType == SpecialType.InkStrike))
        {
            studio.entries.Add(new IconStudio.Entry { prefab = strike, angle = new Vector3(-15f, 30f, 0f), special = true, specialType = SpecialType.InkStrike,
                                                      hide = new[] { "Marker" }, fileName = "InkStrikeSpecial" });
            added++;
        }
        return added;
    }

    // ── Rendering ──────────────────────────────────────────────────────────

    [MenuItem("Tools/Icons/Render Icons")]
    public static void RenderMenu() => Debug.Log(RenderStudioScene());

    // Renders from the studio scene, opening it alongside whatever's open if it isn't already.
    public static string RenderStudioScene()
    {
        IconStudio studio = Object.FindFirstObjectByType<IconStudio>();
        if (studio != null) return Render(studio);
        if (!File.Exists(ScenePath)) BuildScene();
        Scene scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Additive);
        try
        {
            studio = scene.GetRootGameObjects().Select(g => g.GetComponentInChildren<IconStudio>()).First(s => s != null);
            return Render(studio);
        }
        finally
        {
            EditorSceneManager.CloseScene(scene, true);
        }
    }

    public static string Render(IconStudio studio)
    {
        Directory.CreateDirectory(studio.outputFolder);
        var made = new List<string>();
        foreach (IconStudio.Entry e in studio.entries)
        {
            if (e.prefab == null) continue;
            string path = $"{studio.outputFolder}/{(string.IsNullOrEmpty(e.fileName) ? e.prefab.name : e.fileName)}.png";
            Texture2D tex = RenderOne(studio, e);
            if (tex == null) { made.Add($"{e.prefab.name}: nothing to see"); continue; }
            File.WriteAllBytes(path, tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
            Sprite sprite = ImportSprite(path);
            string what = e.special ? $"{e.specialType} special" : e.prefab.name;
            made.Add((e.special ? AssignSpecialIcon(e.specialType, sprite) : AssignIcon(e.prefab, sprite)) ? $"{what} -> {path}" : $"{what} -> {path} (nowhere to set it)");
        }
        AssetDatabase.SaveAssets();
        return $"[Icons] Rendered {made.Count}: " + string.Join("; ", made);
    }

    // One entry, in its own preview scene with copies of the studio's camera and lights; null if
    // it has nothing to show.
    public static Texture2D RenderOne(IconStudio studio, IconStudio.Entry e)
    {
        Scene preview = EditorSceneManager.NewPreviewScene();
        RenderTexture rt = null;
        try
        {
            Camera cam = Object.Instantiate(studio.cam.gameObject).GetComponent<Camera>();
            SceneManager.MoveGameObjectToScene(cam.gameObject, preview);
            cam.scene = preview;
            foreach (Light l in studio.lights)
            {
                if (l == null) continue;
                GameObject copy = Object.Instantiate(l.gameObject, l.transform.position, l.transform.rotation);
                SceneManager.MoveGameObjectToScene(copy, preview);
            }
            rt = new RenderTexture(studio.size, studio.size, 24, RenderTextureFormat.ARGB32) { antiAliasing = 8 };
            cam.targetTexture = rt;

            GameObject item = Object.Instantiate(e.prefab);
            SceneManager.MoveGameObjectToScene(item, preview);
            item.transform.SetPositionAndRotation(Vector3.zero, Quaternion.Euler(e.angle));
            SetParts(item.transform, e.show, true);
            SetParts(item.transform, e.hide, false);
            if (!Pose(item, studio, e, out Rect frame)) return null;
            float half = Mathf.Max(frame.width, frame.height) * 0.5f * studio.padding / e.zoom;
            cam.orthographicSize = half;
            cam.transform.SetPositionAndRotation(new Vector3(frame.center.x - e.offset.x * half * 2f, frame.center.y - e.offset.y * half * 2f, -50f), Quaternion.identity);
            cam.Render();
            cam.targetTexture = null;
            return ReadUnpremultiplied(rt);
        }
        finally
        {
            if (rt != null) Object.DestroyImmediate(rt);
            EditorSceneManager.ClosePreviewScene(preview); // takes the copies and the item with it
        }
    }

    // Shows only its solid meshes, team-tinted parts in the studio's ink colour, and returns what
    // they cover on screen (x, y in world space: the camera looks along +Z).
    static bool Pose(GameObject item, IconStudio studio, IconStudio.Entry e, out Rect frame)
    {
        var tinted = new HashSet<Renderer>();
        if (e.inkParts != null)
            foreach (Renderer r in item.GetComponentsInChildren<Renderer>())
                if (System.Array.IndexOf(e.inkParts, r.name) >= 0) tinted.Add(r);
        if (item.TryGetComponent(out SubDevice sub))
        {
            SerializedProperty list = new SerializedObject(sub).FindProperty("teamTinted");
            for (int i = 0; list != null && i < list.arraySize; i++)
                if (list.GetArrayElementAtIndex(i).objectReferenceValue is Renderer r) tinted.Add(r);
        }
        var block = new MaterialPropertyBlock();
        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        bool any = false;
        foreach (Renderer r in item.GetComponentsInChildren<Renderer>())
        {
            if (!(r is MeshRenderer || r is SkinnedMeshRenderer)) { r.enabled = false; continue; } // trails, particles, aim lines
            if (!r.enabled) continue;
            // Tinted as the game tints them: a sub's team-tinted parts whole (SubDevice), and on
            // weapons just the material slots named Ink... (Roller), leaving the others' colours.
            if (tinted.Contains(r))
            {
                block.Clear();
                block.SetColor("_Color", studio.inkColour);
                block.SetColor("_EmissionColor", studio.inkColour * 0.6f);
                block.SetFloat("_CoreAlpha", 0.35f); // the bubble: faint face-on is lost at icon size
                r.SetPropertyBlock(block);
            }
            else
            {
                Material[] mats = r.sharedMaterials;
                for (int i = 0; i < mats.Length; i++)
                {
                    if (mats[i] == null || !mats[i].name.StartsWith("Ink")) continue;
                    block.Clear();
                    block.SetColor("_Color", studio.inkColour);
                    r.SetPropertyBlock(block, i);
                }
            }
            IEnumerable<Vector3> points = r is MeshRenderer && r.TryGetComponent(out MeshFilter mf) && mf.sharedMesh != null
                ? mf.sharedMesh.vertices.Select(v => r.transform.TransformPoint(v))
                : Corners(r.bounds);
            foreach (Vector3 p in points)
            {
                minX = Mathf.Min(minX, p.x); maxX = Mathf.Max(maxX, p.x);
                minY = Mathf.Min(minY, p.y); maxY = Mathf.Max(maxY, p.y);
                any = true;
            }
        }
        foreach (Light l in item.GetComponentsInChildren<Light>()) l.enabled = false; // the studio lights it
        frame = any ? Rect.MinMaxRect(minX, minY, maxX, maxY) : default;
        return any;
    }

    static void SetParts(Transform root, string[] names, bool on)
    {
        if (names == null) return;
        foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
            if (System.Array.IndexOf(names, t.name) >= 0)
                for (Transform up = t; on && up != root; up = up.parent) up.gameObject.SetActive(true); // and what it's under
        foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
            if (!on && System.Array.IndexOf(names, t.name) >= 0) t.gameObject.SetActive(false);
    }

    static IEnumerable<Vector3> Corners(Bounds b)
    {
        for (int i = 0; i < 8; i++)
            yield return b.center + Vector3.Scale(b.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
    }

    // The render over a clear background has its edges darkened by their coverage: undo that, so
    // the antialiased edges keep the model's colours.
    static Texture2D ReadUnpremultiplied(RenderTexture rt)
    {
        RenderTexture before = RenderTexture.active;
        RenderTexture.active = rt;
        var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false);
        tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
        RenderTexture.active = before;
        Color32[] px = tex.GetPixels32();
        for (int i = 0; i < px.Length; i++)
        {
            Color32 c = px[i];
            if (c.a == 0 || c.a == 255) continue;
            float k = 255f / c.a;
            px[i] = new Color32((byte)Mathf.Min(255, c.r * k), (byte)Mathf.Min(255, c.g * k), (byte)Mathf.Min(255, c.b * k), c.a);
        }
        tex.SetPixels32(px);
        tex.Apply();
        return tex;
    }

    static Sprite ImportSprite(string path)
    {
        AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
        var importer = (TextureImporter)AssetImporter.GetAtPath(path);
        importer.textureType = TextureImporterType.Sprite;
        importer.spriteImportMode = SpriteImportMode.Single;
        importer.alphaIsTransparency = true;
        importer.mipmapEnabled = false;
        importer.SaveAndReimport();
        return AssetDatabase.LoadAssetAtPath<Sprite>(path);
    }

    // A special's icon goes on every player prefab's loadout (there's no prefab of its own).
    static bool AssignSpecialIcon(SpecialType type, Sprite sprite)
    {
        bool any = false;
        foreach (string guid in AssetDatabase.FindAssets("t:Prefab", new[] { "Assets/Prefabs" }))
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GUIDToAssetPath(guid));
            PlayerLoadout loadout = prefab.GetComponentInChildren<PlayerLoadout>(true);
            if (loadout == null) continue;
            var so = new SerializedObject(loadout);
            SerializedProperty icons = so.FindProperty("specialIcons");
            if (icons.arraySize <= (int)type) icons.arraySize = System.Enum.GetValues(typeof(SpecialType)).Length;
            icons.GetArrayElementAtIndex((int)type).objectReferenceValue = sprite;
            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(prefab);
            any = true;
        }
        return any;
    }

    static bool AssignIcon(GameObject prefab, Sprite sprite)
    {
        Component target = prefab.TryGetComponent(out Weapon w) ? w : prefab.TryGetComponent(out SubDevice s) ? s : null;
        if (target == null) return false;
        var so = new SerializedObject(target);
        so.FindProperty("icon").objectReferenceValue = sprite;
        so.ApplyModifiedPropertiesWithoutUndo();
        EditorUtility.SetDirty(prefab);
        return true;
    }
}

[CustomEditor(typeof(IconStudio))]
public class IconStudioEditor : Editor
{
    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();
        EditorGUILayout.Space();
        if (GUILayout.Button("Add missing weapons and subs"))
        {
            Undo.RecordObject(target, "Gather icon entries");
            Debug.Log($"[Icons] Added {IconStudioRenderer.Gather((IconStudio)target)} entries");
            EditorUtility.SetDirty(target);
        }
        if (GUILayout.Button("Render icons")) Debug.Log(IconStudioRenderer.Render((IconStudio)target));
    }
}

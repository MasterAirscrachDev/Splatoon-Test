using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

// Tools > Map > Build Spawn Pad Prefab: the SpawnPad prefab (a small pedestal with a glowing team rim
// and emblem, and the barrier sphere), its sphere mesh and material. Create-only.
// Tools > Map > Add Spawn Pads To Scene: puts a pad under each team's spawn markers in the open
// scene (the markers gathered onto it, a placeholder "Plate" replaced).
// Tools > Map > Upgrade Spawn Pads: migration from the first, large-pad / dome version (prefab and
// the open scene's pads).
public static class SpawnPadBuilder
{
    const string PrefabPath = "Assets/Prefabs/SpawnPad.prefab";
    const string SphereMeshPath = "Assets/Models/SpawnSphere.asset", OldDomeMeshPath = "Assets/Models/SpawnDome.asset";
    const string BarrierMatPath = "Assets/Materials/SpawnBarrier.mat";
    const string MetalPath = "Assets/Materials/Metal.mat", GlowPath = "Assets/Materials/BeaconLight.mat", DarkPath = "Assets/Materials/BeaconBody.mat";
    const float PadRadius = 1.5f, PedestalDepth = 0.3f;  // the prefab's; scenes resize them
    const float BarrierRadius = 3.2f;
    const float MarkerSpread = 0.8f;    // markers from the pad's centre: four players, a capsule (1m) apart
    const float MarkerHeight = 0.4f;    // above the pad's top
    const float PadRim = 0.7f;          // pad radius beyond the markers: a capsule's radius and a margin

    [MenuItem("Tools/Map/Build Spawn Pad Prefab")]
    public static void BuildPrefab()
    {
        if (AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath) != null) { Debug.LogError("[SpawnPad] " + PrefabPath + " exists: change it in place."); return; }

        Material barrier = AssetDatabase.LoadAssetAtPath<Material>(BarrierMatPath);
        if (barrier == null) { barrier = new Material(Shader.Find("Ink/SpawnBarrier")) { name = "SpawnBarrier" }; AssetDatabase.CreateAsset(barrier, BarrierMatPath); }
        Material metal = AssetDatabase.LoadAssetAtPath<Material>(MetalPath), glow = AssetDatabase.LoadAssetAtPath<Material>(GlowPath), dark = AssetDatabase.LoadAssetAtPath<Material>(DarkPath);

        var root = new GameObject("SpawnPad");                       // at the centre of the pad's top
        SpawnPad pad = root.AddComponent<SpawnPad>();

        // The pedestal, walkable: from the top down to the floor.
        GameObject pedestal = Disc("Pedestal", root.transform, metal, PadRadius, -PedestalDepth, 0f);
        pedestal.layer = LayerMask.NameToLayer("Map");
        pedestal.AddComponent<MeshCollider>().sharedMesh = pedestal.GetComponent<MeshFilter>().sharedMesh;
        GameObjectUtility.SetStaticEditorFlags(pedestal, StaticEditorFlags.BatchingStatic | StaticEditorFlags.OccluderStatic | StaticEditorFlags.OccludeeStatic);

        // The top: a glowing team rim round a dark face, a ring emblem in the middle.
        var tinted = new List<Renderer>();
        tinted.Add(Disc("Rim", root.transform, glow, PadRadius + 0.05f, 0f, 0.02f).GetComponent<Renderer>());
        Disc("Face", root.transform, dark, PadRadius - 0.15f, 0f, 0.03f);
        tinted.Add(Disc("Emblem", root.transform, glow, 0.5f, 0f, 0.04f).GetComponent<Renderer>());
        Disc("EmblemCore", root.transform, dark, 0.32f, 0f, 0.05f);

        // The barrier: a sphere curving out of the rim (SpawnPad scales and raises it).
        var domeGo = new GameObject("Dome");
        domeGo.transform.SetParent(root.transform, false);
        domeGo.AddComponent<MeshFilter>().sharedMesh = SphereMesh();
        MeshRenderer dr = domeGo.AddComponent<MeshRenderer>();
        dr.sharedMaterial = barrier;
        dr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        dr.receiveShadows = false;

        var so = new SerializedObject(pad);
        so.FindProperty("dome").objectReferenceValue = dr;
        so.FindProperty("radius").floatValue = BarrierRadius;
        so.FindProperty("padRadius").floatValue = PadRadius;
        SetArray(so.FindProperty("teamTinted"), tinted);
        so.ApplyModifiedPropertiesWithoutUndo();
        PlaceDome(pad, BarrierRadius, PadRadius);

        PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
        Object.DestroyImmediate(root);
        Debug.Log("[SpawnPad] Built " + PrefabPath);
    }

    // The unit sphere asset (made once).
    static Mesh SphereMesh()
    {
        Mesh mesh = AssetDatabase.LoadAssetAtPath<Mesh>(SphereMeshPath);
        if (mesh != null) return mesh;
        mesh = Sphere(48, 24);
        AssetDatabase.CreateAsset(mesh, SphereMeshPath);
        return mesh;
    }

    // A flat cylinder from y0 to y1 (local), no collider.
    static GameObject Disc(string name, Transform parent, Material mat, float radius, float y0, float y1)
    {
        GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cylinder); // radius 0.5, height 2
        go.name = name;
        Object.DestroyImmediate(go.GetComponent<Collider>());
        go.transform.SetParent(parent, false);
        go.transform.localPosition = new Vector3(0f, (y0 + y1) * 0.5f, 0f);
        go.transform.localScale = new Vector3(radius * 2f, (y1 - y0) * 0.5f, radius * 2f);
        Renderer r = go.GetComponent<Renderer>();
        r.sharedMaterial = mat;
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        return go;
    }

    // A unit sphere, normals out.
    static Mesh Sphere(int around, int up)
    {
        var verts = new List<Vector3>();
        var tris = new List<int>();
        for (int j = 0; j <= up; j++)
        {
            float lat = (j / (float)up - 0.5f) * Mathf.PI;
            for (int i = 0; i <= around; i++)
            {
                float lon = i / (float)around * Mathf.PI * 2f;
                verts.Add(new Vector3(Mathf.Cos(lat) * Mathf.Cos(lon), Mathf.Sin(lat), Mathf.Cos(lat) * Mathf.Sin(lon)));
            }
        }
        for (int j = 0; j < up; j++)
            for (int i = 0; i < around; i++)
            {
                int a = j * (around + 1) + i, b = a + around + 1;
                tris.AddRange(new[] { a, b, a + 1, a + 1, b, b + 1 });
            }
        var mesh = new Mesh { name = "SpawnSphere" };
        mesh.SetVertices(verts);
        mesh.SetTriangles(tris, 0);
        mesh.SetNormals(verts);
        mesh.RecalculateBounds();
        return mesh;
    }

    // The sphere scaled to `radius` and raised so it meets the pad's top at `padRadius`.
    static void PlaceDome(SpawnPad pad, float radius, float padRadius)
    {
        Transform dome = pad.transform.Find("Dome");
        dome.localScale = Vector3.one * radius;
        dome.localPosition = Vector3.up * Mathf.Sqrt(Mathf.Max(0f, radius * radius - padRadius * padRadius));
    }

    static void SetArray(SerializedProperty arr, IList<Renderer> items)
    {
        arr.arraySize = items.Count;
        for (int i = 0; i < items.Count; i++) arr.GetArrayElementAtIndex(i).objectReferenceValue = items[i];
    }

    // ── Scene ─────────────────────────────────────────────────────────────

    [MenuItem("Tools/Map/Add Spawn Pads To Scene")]
    public static void AddToScene() => Debug.Log(AddToOpenScene());

    public static string AddToOpenScene(float maxBarrier = BarrierRadius)
    {
        GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
        if (prefab == null) return "[SpawnPad] Build the prefab first.";
        var scene = EditorSceneManager.GetActiveScene();
        var report = new System.Text.StringBuilder("[SpawnPad] " + scene.name + ":");
        Physics.SyncTransforms();

        var teams = new[] { (1, "AlphaSpawn"), (2, "BetaSpawn") };
        var markers = teams.ToDictionary(t => t.Item1, t => GameObject.FindGameObjectsWithTag(t.Item2).OrderBy(g => g.name).ToArray());
        Vector3 Centroid(GameObject[] g) => g.Aggregate(Vector3.zero, (s, m) => s + m.transform.position) / Mathf.Max(1, g.Length);
        var centres = markers.ToDictionary(kv => kv.Key, kv => Centroid(kv.Value));
        foreach (var (team, tag) in teams)
        {
            GameObject[] ms = markers[team];
            if (ms.Length == 0) { report.Append($" no {tag} markers;"); continue; }
            if (ms.Any(m => m.GetComponentInParent<SpawnPad>() != null)) { report.Append($" team {team} already has a pad;"); continue; }
            Vector3 c = centres[team];

            // The pad's top: on the floor (raised a little), where a placeholder plate was if there's one.
            Transform parent = ms[0].transform.parent;
            Transform plate = parent != null ? parent.Find("Plate") : null;
            if (plate != null) Undo.DestroyObjectImmediate(plate.gameObject);
            Physics.SyncTransforms();
            float floor = Physics.Raycast(c + Vector3.up * 0.5f, Vector3.down, out RaycastHit h, 10f, ~0, QueryTriggerInteraction.Ignore) ? h.point.y : c.y - 0.4f;
            float top = floor + 0.12f;
            float padRadius = MarkerSpread + PadRim;

            // The barrier: up to maxBarrier, clear of the other team's.
            float barrier = maxBarrier;
            if (markers[3 - team].Length > 0)
            {
                Vector3 o = centres[3 - team];
                barrier = Mathf.Min(barrier, new Vector2(o.x - c.x, o.z - c.z).magnitude * 0.5f - 0.3f);
            }
            barrier = Mathf.Max(barrier, padRadius + 0.5f);

            var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene);
            Undo.RegisterCreatedObjectUndo(go, "Add Spawn Pad");
            go.name = team == 1 ? "SpawnPadAlpha" : "SpawnPadBeta";
            if (parent != null) go.transform.SetParent(parent, false);
            go.transform.position = new Vector3(c.x, top, c.z);
            SpawnPad pad = go.GetComponent<SpawnPad>();
            Fit(pad, padRadius, top - floor);
            PlaceDome(pad, barrier, padRadius);

            // The markers gathered onto the pad (same directions from its centre), a glowing dot under each.
            var tinted = new List<Renderer>();
            var so = new SerializedObject(pad);
            SerializedProperty arr = so.FindProperty("teamTinted");
            for (int i = 0; i < arr.arraySize; i++) tinted.Add(arr.GetArrayElementAtIndex(i).objectReferenceValue as Renderer);
            Material glow = AssetDatabase.LoadAssetAtPath<Material>(GlowPath);
            for (int i = 0; i < ms.Length; i++)
            {
                Vector3 off = ms[i].transform.position - c; off.y = 0f;
                if (off.sqrMagnitude < 1e-4f) off = Quaternion.Euler(0f, 90f * i, 0f) * Vector3.forward;
                Undo.SetTransformParent(ms[i].transform, go.transform, "Add Spawn Pad");
                Undo.RecordObject(ms[i].transform, "Add Spawn Pad");
                ms[i].transform.localPosition = off.normalized * MarkerSpread + Vector3.up * MarkerHeight;
                GameObject dot = Disc("MarkerDot" + i, go.transform, glow, 0.22f, 0f, 0.045f);
                dot.transform.localPosition = new Vector3(ms[i].transform.localPosition.x, dot.transform.localPosition.y, ms[i].transform.localPosition.z);
                tinted.Add(dot.GetComponent<Renderer>());
            }
            so.FindProperty("team").intValue = team;
            so.FindProperty("radius").floatValue = barrier;
            so.FindProperty("padRadius").floatValue = padRadius;
            SetArray(arr, tinted);
            so.ApplyModifiedProperties();
            report.Append($" {go.name} pad r {padRadius:F2} on {floor:F2}, barrier r {barrier:F2} (top {top + Mathf.Sqrt(barrier * barrier - padRadius * padRadius) + barrier:F1}), {ms.Length} markers;");
        }
        EditorSceneManager.MarkSceneDirty(scene);
        return report.ToString();
    }

    // Sizes the pad's parts (made for PadRadius and PedestalDepth).
    static void Fit(SpawnPad pad, float radius, float depth)
    {
        float k = radius / PadRadius;
        foreach (Transform part in pad.transform)
        {
            if (part.name == "Dome") continue;
            Vector3 s = part.localScale;
            part.localScale = new Vector3(s.x * k, s.y, s.z * k);
            if (part.name == "Pedestal")
            {
                part.localScale = new Vector3(part.localScale.x, depth * 0.5f, part.localScale.z);
                part.localPosition = new Vector3(0f, -depth * 0.5f, 0f);
            }
        }
    }

    // ── Migration: the first version (2.2m pad, 4.5m hemisphere dome) ───────

    [MenuItem("Tools/Map/Upgrade Spawn Pads")]
    public static void Upgrade() => Debug.Log(UpgradePrefab() + "\n" + UpgradeOpenScene());

    // The prefab in place: smaller pad and emblem, the sphere barrier.
    public static string UpgradePrefab()
    {
        GameObject root = PrefabUtility.LoadPrefabContents(PrefabPath);
        SpawnPad pad = root.GetComponent<SpawnPad>();
        var so = new SerializedObject(pad);
        if (Mathf.Approximately(so.FindProperty("padRadius").floatValue, PadRadius) && root.transform.Find("Dome").GetComponent<MeshFilter>().sharedMesh == AssetDatabase.LoadAssetAtPath<Mesh>(SphereMeshPath))
        {
            PrefabUtility.UnloadPrefabContents(root);
            return "[SpawnPad] prefab already upgraded";
        }
        const float OldPad = 2.2f;
        var sizes = new Dictionary<string, float> { { "Pedestal", PadRadius }, { "Rim", PadRadius + 0.05f }, { "Face", PadRadius - 0.15f }, { "Emblem", 0.5f }, { "EmblemCore", 0.32f } };
        foreach (Transform part in root.transform)
            if (sizes.TryGetValue(part.name, out float r)) part.localScale = new Vector3(r * 2f, part.localScale.y, r * 2f);
        root.transform.Find("Dome").GetComponent<MeshFilter>().sharedMesh = SphereMesh();
        so.FindProperty("radius").floatValue = BarrierRadius;
        so.FindProperty("padRadius").floatValue = PadRadius;
        so.ApplyModifiedPropertiesWithoutUndo();
        PlaceDome(pad, BarrierRadius, PadRadius);
        PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
        PrefabUtility.UnloadPrefabContents(root);
        if (AssetDatabase.LoadAssetAtPath<Mesh>(OldDomeMeshPath) != null) AssetDatabase.DeleteAsset(OldDomeMeshPath);
        return $"[SpawnPad] prefab: pad {OldPad} -> {PadRadius}, sphere barrier r {BarrierRadius}";
    }

    // The open scene's pads: markers back out, pads removed, then placed afresh.
    public static string UpgradeOpenScene()
    {
        var pads = Object.FindObjectsByType<SpawnPad>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        foreach (SpawnPad pad in pads)
        {
            Transform parent = pad.transform.parent;
            foreach (Transform child in pad.transform.Cast<Transform>().ToArray())
                if (child.CompareTag("AlphaSpawn") || child.CompareTag("BetaSpawn"))
                    Undo.SetTransformParent(child, parent, "Upgrade Spawn Pads");
            Undo.DestroyObjectImmediate(pad.gameObject);
        }
        return $"removed {pads.Length} old pads; " + AddToOpenScene();
    }
}

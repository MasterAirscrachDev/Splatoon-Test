using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

// Tools/Fluid/Build Fluid Lab Scene: Assets/Fluid/Lab/FluidLab.unity (create-only), the place to
// tune the screen-space fluid (FluidRenderer) apart from the game: a floor and a few blocks to see
// through the ink, the sun, a camera with the renderer, and the FluidLab emitters. No networking,
// no Steam: safe to play.
public static class FluidLabBuilder
{
    const string ScenePath = "Assets/Fluid/Lab/FluidLab.unity";
    const string Folder = "Assets/Fluid/Lab";

    [MenuItem("Tools/Fluid/Build Fluid Lab Scene")]
    public static void Build()
    {
        if (System.IO.File.Exists(ScenePath)) { Debug.LogWarning($"[Fluid] {ScenePath} exists already: not rebuilt"); return; }
        var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);

        Material floor = SaveMaterial("LabFloor", new Color(0.82f, 0.82f, 0.8f), 0.2f);
        Material block = SaveMaterial("LabBlock", new Color(0.35f, 0.38f, 0.45f), 0.4f);
        Material stripe = SaveMaterial("LabStripe", new Color(0.95f, 0.95f, 0.95f), 0.1f);

        Make(PrimitiveType.Plane, "Floor", new Vector3(0f, 0f, 8f), new Vector3(5f, 1f, 4f), floor); // 50m wide: room for the row of weapons
        // Stripes on the floor and blocks behind: something to see bent through the ink.
        for (int i = -4; i <= 4; i++)
            Make(PrimitiveType.Cube, "Stripe", new Vector3(i * 2f, 0.005f, 8f), new Vector3(0.25f, 0.01f, 30f), stripe).GetComponent<Collider>().enabled = false;
        Make(PrimitiveType.Cube, "BackWall", new Vector3(0f, 2f, 22f), new Vector3(30f, 4f, 0.5f), block);
        Make(PrimitiveType.Cube, "Block", new Vector3(-4f, 0.75f, 9f), new Vector3(1.5f, 1.5f, 1.5f), block);
        Make(PrimitiveType.Cube, "Block", new Vector3(4f, 1f, 12f), new Vector3(2f, 2f, 2f), block);

        GameObject camGo = Camera.main.gameObject;
        camGo.transform.SetPositionAndRotation(new Vector3(0f, 8f, -12f), Quaternion.LookRotation(new Vector3(0f, 0.5f, 7f) - new Vector3(0f, 8f, -12f)));
        Camera cam = camGo.GetComponent<Camera>();
        cam.fieldOfView = 60f;
        camGo.AddComponent<FluidRenderer>();
        camGo.AddComponent<FlyCamera>();

        Light sun = Object.FindFirstObjectByType<Light>();
        sun.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
        RenderSettings.sun = sun;

        var lab = new GameObject("FluidLab").AddComponent<FluidLab>();
        var so = new SerializedObject(lab);
        so.FindProperty("projectilePrefab").objectReferenceValue = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/Projectile.prefab");
        so.ApplyModifiedPropertiesWithoutUndo();
        AssignWeapons(lab);

        EditorSceneManager.SaveScene(scene, ScenePath);
        Debug.Log($"[Fluid] Built {ScenePath}");
    }

    static Material SaveMaterial(string name, Color colour, float smoothness)
    {
        string path = $"{Folder}/{name}.mat";
        var m = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (m != null) return m;
        m = new Material(Shader.Find("Standard")) { color = colour };
        m.SetFloat("_Glossiness", smoothness);
        AssetDatabase.CreateAsset(m, path);
        return m;
    }

    static GameObject Make(PrimitiveType type, string name, Vector3 at, Vector3 scale, Material mat)
    {
        GameObject go = GameObject.CreatePrimitive(type);
        go.name = name;
        go.transform.position = at;
        go.transform.localScale = scale;
        Renderer r = go.GetComponent<Renderer>();
        r.sharedMaterial = mat;
        r.shadowCastingMode = ShadowCastingMode.On;
        return go;
    }

    // In place: the lab's floor and back wall inkable, as the game's maps are (the ink material with
    // an underlay, the InkSurface layer, a SurfaceInkManager), on quads whose UVs are in proportion to
    // their metres (so splats stay round); the floor's stripes go (they'd sit on the ink).
    [MenuItem("Tools/Fluid/Make Lab Inkable")]
    public static void MakeInkable()
    {
        var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        foreach (GameObject go in scene.GetRootGameObjects())
            if (go.name == "Stripe" || go.name == "BackWall" || go.name == "InkWall") Object.DestroyImmediate(go);
        Material ink = InkMaterial();
        GameObject floor = GameObject.Find("Floor");
        floor.transform.SetPositionAndRotation(new Vector3(0f, 0f, 8f), Quaternion.identity);
        floor.transform.localScale = Vector3.one;
        Inkable(floor, Quad("LabFloorQuad", new Vector2(50f, 40f), floor: true), ink);
        var wall = new GameObject("InkWall");
        wall.transform.SetPositionAndRotation(new Vector3(0f, 0f, 22f), Quaternion.identity);
        Inkable(wall, Quad("LabWallQuad", new Vector2(50f, 8f), floor: false), ink);
        EditorSceneManager.SaveScene(scene);
        Debug.Log("[Fluid] The lab's floor and back wall are inkable");
    }

    static Material InkMaterial()
    {
        string path = $"{Folder}/LabInk.mat";
        if (!System.IO.File.Exists(path)) AssetDatabase.CopyAsset("Assets/Materials/InkMaterial 1.mat", path);
        return AssetDatabase.LoadAssetAtPath<Material>(path);
    }

    static void Inkable(GameObject go, Mesh mesh, Material ink)
    {
        foreach (Collider c in go.GetComponents<Collider>()) Object.DestroyImmediate(c);
        if (!go.TryGetComponent(out MeshFilter mf)) mf = go.AddComponent<MeshFilter>();
        mf.sharedMesh = mesh;
        if (!go.TryGetComponent(out MeshRenderer r)) r = go.AddComponent<MeshRenderer>();
        r.sharedMaterial = ink;
        go.AddComponent<MeshCollider>().sharedMesh = mesh;
        go.layer = LayerMask.NameToLayer("InkSurface");
        if (go.GetComponent<SurfaceInkManager>() == null) go.AddComponent<SurfaceInkManager>();
    }

    // A flat quad `size` metres: the floor lies in XZ (facing up, centred), the wall stands in XY on
    // y = 0 (facing -z, centred across). UVs in metres over the longer side, so a metre's a metre.
    static Mesh Quad(string name, Vector2 size, bool floor)
    {
        string path = $"{Folder}/{name}.asset";
        var old = AssetDatabase.LoadAssetAtPath<Mesh>(path);
        if (old != null) AssetDatabase.DeleteAsset(path);
        float hx = size.x * 0.5f, span = Mathf.Max(size.x, size.y);
        var m = new Mesh { name = name };
        if (floor)
        {
            float hz = size.y * 0.5f;
            m.vertices = new[] { new Vector3(-hx, 0, -hz), new Vector3(hx, 0, -hz), new Vector3(hx, 0, hz), new Vector3(-hx, 0, hz) };
            m.triangles = new[] { 0, 2, 1, 0, 3, 2 };
        }
        else
        {
            m.vertices = new[] { new Vector3(-hx, 0, 0), new Vector3(hx, 0, 0), new Vector3(hx, size.y, 0), new Vector3(-hx, size.y, 0) };
            m.triangles = new[] { 0, 2, 1, 0, 3, 2 };
        }
        m.uv = new[] { new Vector2(0, 0), new Vector2(size.x / span, 0), new Vector2(size.x / span, size.y / span), new Vector2(0, size.y / span) };
        m.RecalculateNormals();
        if (floor ? m.normals[0].y < 0 : m.normals[0].z > 0) { m.triangles = new[] { 0, 1, 2, 0, 2, 3 }; m.RecalculateNormals(); } // face up / towards -z
        m.RecalculateTangents();
        m.RecalculateBounds();
        AssetDatabase.CreateAsset(m, path);
        return m;
    }

    // The lab mimics the player's loadout: its weapon prefabs, in its order.
    public static void AssignWeapons(FluidLab lab)
    {
        var player = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/PlayerEntity - VOID.prefab");
        var loadout = new SerializedObject(player.GetComponentInChildren<PlayerLoadout>(true)).FindProperty("weapons");
        var so = new SerializedObject(lab);
        var list = so.FindProperty("weapons");
        list.arraySize = loadout.arraySize;
        for (int i = 0; i < loadout.arraySize; i++) list.GetArrayElementAtIndex(i).objectReferenceValue = loadout.GetArrayElementAtIndex(i).objectReferenceValue;
        so.ApplyModifiedPropertiesWithoutUndo();
    }
}

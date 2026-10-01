using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using static ClimbTestLayout;
using Face = ClimbTestBox.Face;

// Generates ClimbTest.unity: the test stations, a dev-mode NetGameManager (local player, no
// Steam) and a ClimbTestRunner. Regenerate rather than hand-editing the scene.
public static class ClimbTestSceneBuilder
{
    const string Root      = "Assets/Tests/ClimbTest";
    const string ScenePath = Root + "/ClimbTest.unity";
    const string MeshDir   = Root + "/Meshes";
    const string InkMaterialPath = "Assets/Materials/InkMaterial 1.mat";
    const string PlayerPrefabPath = "Assets/Prefabs/PlayerEntity - VOID.prefab";
    const string ProjectilePrefabPath = "Assets/Prefabs/Projectile.prefab";
    const int Own = 1, Enemy = 2;

    static Material inkMaterial;
    static int inkLayer;

    [MenuItem("Tools/Climb Test/Build Test Scene")]
    public static void Build()
    {
        for (int i = 0; i < SceneManager.sceneCount; i++)
            if (SceneManager.GetSceneAt(i).isDirty)
            {
                Debug.LogError("[ClimbTest] Save or discard changes to open scenes before building the climb test scene.");
                return;
            }

        // Team colours from the open game scene, if any.
        Color alpha = Color.cyan, beta = Color.magenta;
        NetGameManager existing = Object.FindFirstObjectByType<NetGameManager>();
        if (existing != null)
        {
            alpha = existing.AlphaTeam;
            beta  = existing.BetaTeam;
        }

        inkMaterial = AssetDatabase.LoadAssetAtPath<Material>(InkMaterialPath);
        inkLayer = LayerMask.NameToLayer("InkSurface");
        if (!AssetDatabase.IsValidFolder(MeshDir)) AssetDatabase.CreateFolder(Root, "Meshes");

        Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

        GameObject light = new GameObject("Directional Light");
        Light l = light.AddComponent<Light>();
        l.type = LightType.Directional;
        l.shadows = LightShadows.Soft;
        light.transform.rotation = Quaternion.Euler(50f, -30f, 0f);

        GameObject core = new GameObject("GameCore");
        NetGameManager gm = core.AddComponent<NetGameManager>();
        SerializedObject so = new SerializedObject(gm);
        so.FindProperty("playerEntityPrefab").objectReferenceValue = AssetDatabase.LoadAssetAtPath<GameObject>(PlayerPrefabPath);
        so.FindProperty("devMode").boolValue = true;   // spawns a local player at the origin, no Steam
        so.FindProperty("autoLobby").boolValue = false;
        so.FindProperty("alphaTeam").colorValue = alpha;
        so.FindProperty("betaTeam").colorValue = beta;
        so.ApplyModifiedPropertiesWithoutUndo();

        ClimbTestRunner runner = new GameObject("ClimbTestRunner").AddComponent<ClimbTestRunner>();
        SerializedObject rso = new SerializedObject(runner);
        rso.FindProperty("projectilePrefab").objectReferenceValue = AssetDatabase.LoadAssetAtPath<GameObject>(ProjectilePrefabPath);
        rso.ApplyModifiedPropertiesWithoutUndo();

        foreach (Station s in System.Enum.GetValues(typeof(Station)))
            BuildStation(s);

        EditorSceneManager.SaveScene(scene, ScenePath);
        AssetDatabase.SaveAssets();
        Debug.Log($"[ClimbTest] Built {ScenePath}");
    }

    static void BuildStation(Station s)
    {
        Transform st = new GameObject(Name(s)).transform;
        st.position = Origin(s);

        Box("Floor", st, new Vector3(0f, -FloorThickness / 2f, 0f), new Vector3(FloorWidth, FloorThickness, FloorDepth),
            Quaternion.identity, Fill(new Rect(0, 0, 1, 1), Own));

        Vector3 spawn = new Vector3(0f, 0.05f, -6f);
        float spawnYaw = 0f;
        // 8m deep so a pop over the top lip has room to land.
        Vector3 wall = new Vector3(8f, WallHeight, 8f);
        Vector3 wallCenter = new Vector3(0f, WallHeight / 2f, 4f);

        switch (s)
        {
            case Station.Lobby:
                spawn = new Vector3(0f, 0.05f, 0f);
                break;
            case Station.FlatWall:
                Box("Wall", st, wallCenter, wall, Quaternion.identity, Fill(new Rect(0, 0, 1, 1), Own));
                break;
            case Station.Ledge:
                Box("Ledge", st, new Vector3(0f, LedgeHeight / 2f, 4f), new Vector3(8f, LedgeHeight, 8f),
                    Quaternion.identity, Fill(new Rect(0, 0, 1, 1), Own));
                spawn = new Vector3(0f, LedgeHeight + 0.05f, 5f); // on top, facing the front edge
                spawnYaw = 180f;
                break;
            case Station.Pillar:
                Box("Pillar", st, new Vector3(0f, WallHeight / 2f, 2f), new Vector3(PillarWidth, WallHeight, 4f),
                    Quaternion.identity, Fill(new Rect(0, 0, 1, 1), Own));
                spawn = new Vector3(PillarWidth / 2f - 0.8f, 0.05f, -5f); // near the right-hand corner
                break;
            case Station.InnerCorner:
                Box("FrontWall", st, new Vector3(0f, WallHeight / 2f, 0.5f), new Vector3(8f, WallHeight, 1f),
                    Quaternion.identity, Fill(new Rect(0, 0, 1, 1), Own));
                Box("SideWall", st, new Vector3(InnerCornerSideX + 0.5f, WallHeight / 2f, -3f), new Vector3(1f, WallHeight, 6f),
                    Quaternion.identity, Fill(new Rect(0, 0, 1, 1), Own));
                spawn = new Vector3(InnerCornerSideX - 1.2f, 0.05f, -5f);
                break;
            case Station.NeutralWall:
                Box("Wall", st, wallCenter, wall, Quaternion.identity);
                break;
            case Station.EnemyWall:
                Box("Wall", st, wallCenter, wall, Quaternion.identity, Fill(new Rect(0, 0, 1, 1), Enemy));
                break;
            case Station.PartialWall:
                Box("Wall", st, wallCenter, wall, Quaternion.identity,
                    Fill(ClimbTestBox.FaceRectBottom(Face.Front, PartialInkHeight / WallHeight), Own));
                break;
            case Station.Ramp30:
            case Station.Ramp60:
            {
                float angle  = s == Station.Ramp30 ? 30f : 60f;
                float length = s == Station.Ramp30 ? Ramp30Length : Ramp60Length;
                Box("Ramp", st, RampCenter(angle, length), new Vector3(RampWidth, RampThickness, length),
                    RampRotation(angle), Fill(new Rect(0, 0, 1, 1), Own));
                // Second spawn near the top, facing downhill, for descent tests.
                Marker("SpawnTop", st, RampSurfacePoint(angle, length, length - 1.5f) + Vector3.up * 0.05f, 180f);
                break;
            }
        }

        Marker("Spawn", st, spawn, spawnYaw);
        Label(st, s.ToString());
    }

    static TestInkFill.Region[] Fill(Rect uv, int team) => new[] { new TestInkFill.Region { uv = uv, team = team } };

    static void Box(string name, Transform parent, Vector3 localCenter, Vector3 size, Quaternion rot, TestInkFill.Region[] fill = null)
    {
        GameObject go = new GameObject(name);
        go.layer = inkLayer;
        // Fully static like the real map, so static batching is exercised too.
        GameObjectUtility.SetStaticEditorFlags(go, (StaticEditorFlags)~0);
        go.transform.SetParent(parent, false);
        go.transform.localPosition = localCenter;
        go.transform.localRotation = rot;

        Mesh mesh = BoxMesh(size);
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        go.AddComponent<MeshRenderer>().sharedMaterial = inkMaterial;
        go.AddComponent<MeshCollider>().sharedMesh = mesh; // non-convex, so raycasts report textureCoord

        SurfaceInkManager ink = go.AddComponent<SurfaceInkManager>();
        SerializedObject so = new SerializedObject(ink);
        // ~512² ink texture whatever the box size.
        so.FindProperty("pixelsPerUnit").floatValue = 400f / Mathf.Max(size.x, size.y, size.z);
        so.FindProperty("splatScale").floatValue = 1f;
        so.ApplyModifiedPropertiesWithoutUndo();

        if (fill != null)
            go.AddComponent<TestInkFill>().regions = fill;
    }

    static void Marker(string name, Transform parent, Vector3 localPos, float yaw)
    {
        Transform t = new GameObject(name).transform;
        t.SetParent(parent, false);
        t.localPosition = localPos;
        t.localRotation = Quaternion.Euler(0f, yaw, 0f);
    }

    static void Label(Transform parent, string text)
    {
        GameObject go = new GameObject("Label");
        go.transform.SetParent(parent, false);
        go.transform.localPosition = new Vector3(0f, WallHeight + 2f, -1f);
        TextMesh tm = go.AddComponent<TextMesh>();
        Font font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        tm.font = font;
        tm.text = text;
        tm.characterSize = 0.25f;
        tm.fontSize = 64;
        tm.anchor = TextAnchor.MiddleCenter;
        go.GetComponent<MeshRenderer>().sharedMaterial = font.material;
    }

    // Box with one UV island per face (see ClimbTestBox), cached as an asset per size.
    static Mesh BoxMesh(Vector3 size)
    {
        string path = $"{MeshDir}/Box_{size.x:0.##}x{size.y:0.##}x{size.z:0.##}.asset";
        Mesh cached = AssetDatabase.LoadAssetAtPath<Mesh>(path);
        if (cached != null) return cached;

        var verts = new List<Vector3>();
        var norms = new List<Vector3>();
        var uvs   = new List<Vector2>();
        var tris  = new List<int>();
        Vector3 half = size * 0.5f;

        for (int f = 0; f < 6; f++)
        {
            ClimbTestBox.FaceAxes((Face)f, out Vector3 n, out Vector3 u, out Vector3 v);
            Rect r = ClimbTestBox.FaceRect((Face)f);
            int b = verts.Count;
            for (int j = 0; j < 4; j++)
            {
                float s = j & 1, t = j >> 1; // 0 = bottom-left, 1 = bottom-right, 2 = top-left, 3 = top-right
                verts.Add(Vector3.Scale(n + u * (s * 2f - 1f) + v * (t * 2f - 1f), half));
                norms.Add(n);
                uvs.Add(new Vector2(r.x + s * r.width, r.y + t * r.height));
            }
            tris.AddRange(new[] { b, b + 2, b + 1, b + 2, b + 3, b + 1 }); // clockwise from outside
        }

        Mesh mesh = new Mesh { name = System.IO.Path.GetFileNameWithoutExtension(path) };
        mesh.SetVertices(verts);
        mesh.SetNormals(norms);
        mesh.SetUVs(0, uvs);
        mesh.SetTriangles(tris, 0);
        mesh.RecalculateBounds();
        mesh.RecalculateTangents(); // the ink surface shader is normal-mapped
        AssetDatabase.CreateAsset(mesh, path);
        return mesh;
    }
}

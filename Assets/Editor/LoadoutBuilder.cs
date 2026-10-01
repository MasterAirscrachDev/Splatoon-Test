using System.IO;
using UnityEditor;
using UnityEngine;

// Builds the sub/special assets: Assets/Prefabs/Beacon.prefab, the bubble shield material, and
// the PlayerLoadout (with its bubble) on the player prefab. Safe to re-run.
public static class LoadoutBuilder
{
    const string BeaconPath = "Assets/Prefabs/Beacon.prefab";
    const string PlayerPath = "Assets/Prefabs/PlayerEntity - VOID.prefab";
    const string BubbleMatPath = "Assets/Materials/Bubble.mat";
    const string BeaconBodyMatPath = "Assets/Materials/BeaconBody.mat";
    const string BeaconLightMatPath = "Assets/Materials/BeaconLight.mat";

    [MenuItem("Tools/Gameplay/Build Loadout (Beacon, Bubble Shield)")]
    public static void Build()
    {
        Beacon beacon = BuildBeacon();
        Material bubble = EnsureMaterial(BubbleMatPath, "Ink/Bubble", null);
        AddToPlayer(beacon, bubble);
        AssetDatabase.SaveAssets();
        Debug.Log($"[Loadout] Built {BeaconPath} and updated {PlayerPath}");
    }

    // A small post with a spinning team-coloured light on top. Only the root's trigger collides
    // (on the Hitbox layer, so projectiles hit it but movement and ink raycasts ignore it).
    static Beacon BuildBeacon()
    {
        Material body = EnsureMaterial(BeaconBodyMatPath, "Standard", m => m.color = new Color(0.22f, 0.22f, 0.26f));
        Material light = EnsureMaterial(BeaconLightMatPath, "Standard", m =>
        {
            m.EnableKeyword("_EMISSION");
            m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.None;
            m.SetColor("_EmissionColor", Color.white * 0.6f);
            m.SetFloat("_Glossiness", 0.8f);
        });

        var root = new GameObject("Beacon");
        root.layer = LayerMask.NameToLayer("Hitbox");
        var trigger = root.AddComponent<CapsuleCollider>();
        trigger.isTrigger = true;
        trigger.center = new Vector3(0f, 0.6f, 0f);
        trigger.height = 1.3f;
        trigger.radius = 0.35f;

        Part(PrimitiveType.Cylinder, "Base", root.transform, new Vector3(0f, 0.06f, 0f), new Vector3(0.6f, 0.06f, 0.6f), body);
        Part(PrimitiveType.Cylinder, "Post", root.transform, new Vector3(0f, 0.45f, 0f), new Vector3(0.12f, 0.35f, 0.12f), body);
        var spinner = new GameObject("Spinner").transform;
        spinner.SetParent(root.transform, false);
        spinner.localPosition = new Vector3(0f, 0.95f, 0f);
        Renderer orb = Part(PrimitiveType.Sphere, "Light", spinner, Vector3.zero, Vector3.one * 0.32f, light);
        Renderer ring = Part(PrimitiveType.Cylinder, "Ring", spinner, Vector3.zero, new Vector3(0.6f, 0.015f, 0.6f), light);
        ring.transform.localRotation = Quaternion.Euler(20f, 0f, 0f); // tilted, so the spin reads
        Renderer foot = Part(PrimitiveType.Cylinder, "Foot", root.transform, new Vector3(0f, 0.125f, 0f), new Vector3(0.45f, 0.01f, 0.45f), light);

        Beacon beacon = root.AddComponent<Beacon>();
        var so = new SerializedObject(beacon);
        SerializedProperty tinted = so.FindProperty("teamTinted");
        Renderer[] parts = { orb, ring, foot };
        tinted.arraySize = parts.Length;
        for (int i = 0; i < parts.Length; i++) tinted.GetArrayElementAtIndex(i).objectReferenceValue = parts[i];
        so.FindProperty("spinner").objectReferenceValue = spinner;
        so.ApplyModifiedPropertiesWithoutUndo();

        Directory.CreateDirectory(Path.GetDirectoryName(BeaconPath));
        GameObject saved = PrefabUtility.SaveAsPrefabAsset(root, BeaconPath);
        Object.DestroyImmediate(root);
        return saved.GetComponent<Beacon>();
    }

    static Renderer Part(PrimitiveType type, string name, Transform parent, Vector3 pos, Vector3 scale, Material mat)
    {
        GameObject go = GameObject.CreatePrimitive(type);
        Object.DestroyImmediate(go.GetComponent<Collider>()); // visual only
        go.name = name;
        go.transform.SetParent(parent, false);
        go.transform.localPosition = pos;
        go.transform.localScale = scale;
        Renderer r = go.GetComponent<Renderer>();
        r.sharedMaterial = mat;
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
        return r;
    }

    // Adds PlayerLoadout and a bubble shield sphere to the player prefab, replacing old ones.
    static void AddToPlayer(Beacon beaconPrefab, Material bubbleMat)
    {
        GameObject contents = PrefabUtility.LoadPrefabContents(PlayerPath);
        try
        {
            PlayerController pc = contents.GetComponentInChildren<PlayerController>(true);
            PlayerLoadout loadout = pc.GetComponent<PlayerLoadout>();
            if (loadout == null) loadout = pc.gameObject.AddComponent<PlayerLoadout>();

            Transform old = pc.transform.Find("BubbleShield");
            if (old != null) Object.DestroyImmediate(old.gameObject);
            GameObject bubble = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            Object.DestroyImmediate(bubble.GetComponent<Collider>());
            bubble.name = "BubbleShield";
            bubble.transform.SetParent(pc.transform, false);
            bubble.transform.localPosition = Vector3.zero;
            bubble.transform.localScale = Vector3.one * 2.6f;
            Renderer r = bubble.GetComponent<Renderer>();
            r.sharedMaterial = bubbleMat;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            bubble.SetActive(false);

            var so = new SerializedObject(loadout);
            so.FindProperty("beaconPrefab").objectReferenceValue = beaconPrefab;
            so.FindProperty("shieldVisual").objectReferenceValue = bubble.transform;
            so.ApplyModifiedPropertiesWithoutUndo();

            PrefabUtility.SaveAsPrefabAsset(contents, PlayerPath);
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(contents);
        }
    }

    static Material EnsureMaterial(string path, string shaderName, System.Action<Material> setup)
    {
        Material mat = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (mat == null)
        {
            mat = new Material(Shader.Find(shaderName));
            setup?.Invoke(mat);
            AssetDatabase.CreateAsset(mat, path);
        }
        return mat;
    }
}

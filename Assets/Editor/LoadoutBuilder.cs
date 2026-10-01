using System.IO;
using UnityEditor;
using UnityEngine;

// Builds the loadout assets: the weapon prefabs (Assets/Prefabs/Weapons), the Beacon and
// Sprinkler prefabs, the bubble shield, and the PlayerLoadout on the player prefab. Prefabs and
// the bubble are only created when missing, so tuning done on them survives a re-run.
public static class LoadoutBuilder
{
    const string BeaconPath = "Assets/Prefabs/Beacon.prefab";
    const string SprinklerPath = "Assets/Prefabs/Sprinkler.prefab";
    const string ProjectilePath = "Assets/Prefabs/Projectile.prefab";
    const string AirsprayPath = "Assets/Prefabs/Weapons/AirspraySE.prefab";
    const string InkshotPath = "Assets/Prefabs/Weapons/Inkshot.prefab";
    const string PlayerPath = "Assets/Prefabs/PlayerEntity - VOID.prefab";
    const string BubbleMatPath = "Assets/Materials/Bubble.mat";
    const string BeaconBodyMatPath = "Assets/Materials/BeaconBody.mat";
    const string BeaconLightMatPath = "Assets/Materials/BeaconLight.mat";

    [MenuItem("Tools/Gameplay/Build Loadout (Weapons, Subs, Bubble Shield)")]
    public static void Build()
    {
        Beacon beacon = AssetDatabase.LoadAssetAtPath<Beacon>(BeaconPath);
        if (beacon == null) beacon = BuildBeacon();
        Sprinkler sprinkler = AssetDatabase.LoadAssetAtPath<Sprinkler>(SprinklerPath);
        if (sprinkler == null) sprinkler = BuildSprinkler();
        Material bubble = EnsureMaterial(BubbleMatPath, "Ink/Bubble", null);
        AddToPlayer(beacon, sprinkler, bubble);
        AssetDatabase.SaveAssets();
        Debug.Log($"[Loadout] Loadout assets ready; updated {PlayerPath}");
    }

    // Airspray SE: the player's original gun, moved into its own prefab with its tuned stats.
    static Weapon CreateAirspray(Transform oldGun)
    {
        GameObject copy = Object.Instantiate(oldGun.gameObject, oldGun.parent);
        copy.transform.SetParent(null, false); // still in the prefab's preview scene
        copy.name = "AirspraySE";
        copy.transform.localPosition = Vector3.zero;
        copy.transform.localRotation = Quaternion.identity;
        var so = new SerializedObject(copy.GetComponent<WeaponShooter>());
        so.FindProperty("displayName").stringValue = "Airspray SE";
        so.FindProperty("description").stringValue = "Fast, wide spray that covers the ground quickly up close.";
        so.FindProperty("fireRate").floatValue = 0.1f;
        so.FindProperty("range").floatValue = 10f;
        so.FindProperty("splashSize").intValue = 11;
        so.FindProperty("damage").floatValue = 30f;
        so.FindProperty("inkCostPerShot").floatValue = 16f;
        so.FindProperty("yawSpread").floatValue = 15f;
        so.FindProperty("pitchSpread").floatValue = 2f;
        so.FindProperty("fillerSpread").floatValue = 10f;
        so.FindProperty("muzzle").objectReferenceValue = copy.transform.Find("BulletSpawn");
        if (so.FindProperty("projectile").objectReferenceValue == null)
            so.FindProperty("projectile").objectReferenceValue = AssetDatabase.LoadAssetAtPath<GameObject>(ProjectilePath);
        so.ApplyModifiedPropertiesWithoutUndo();

        Directory.CreateDirectory(Path.GetDirectoryName(AirsprayPath));
        GameObject saved = PrefabUtility.SaveAsPrefabAsset(copy, AirsprayPath);
        Object.DestroyImmediate(copy);
        return saved.GetComponent<Weapon>();
    }

    // Inkshot: Airspray SE's set-up with a longer, slimmer placeholder model and its own stats.
    static Weapon CreateInkshot()
    {
        GameObject c = PrefabUtility.LoadPrefabContents(AirsprayPath);
        try
        {
            c.name = "Inkshot";
            var so = new SerializedObject(c.GetComponent<WeaponShooter>());
            so.FindProperty("displayName").stringValue = "Inkshot";
            so.FindProperty("description").stringValue = "Narrow stream with more reach, at a slower rate of fire.";
            so.FindProperty("fireRate").floatValue = 0.18f;
            so.FindProperty("range").floatValue = 15f;
            so.FindProperty("yawSpread").floatValue = 3f;
            so.FindProperty("pitchSpread").floatValue = 0.5f;
            so.FindProperty("fillerSpread").floatValue = 3f;
            so.ApplyModifiedPropertiesWithoutUndo();
            Transform model = c.transform.Find("InkShot");
            if (model != null) model.localScale = Vector3.Scale(model.localScale, new Vector3(0.8f, 0.8f, 1.6f));
            Transform muzzle = c.transform.Find("BulletSpawn");
            if (muzzle != null) muzzle.localPosition += new Vector3(0f, 0f, 0.25f);
            PrefabUtility.SaveAsPrefabAsset(c, InkshotPath);
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(c);
        }
        return AssetDatabase.LoadAssetAtPath<Weapon>(InkshotPath);
    }

    // A squat base with a spinning head: hub and two arms with nozzles at their ends, in the team
    // colour. Like the beacon, only the root's trigger collides (Hitbox layer).
    static Sprinkler BuildSprinkler()
    {
        Material body = EnsureMaterial(BeaconBodyMatPath, "Standard", m => m.color = new Color(0.22f, 0.22f, 0.26f));
        Material light = AssetDatabase.LoadAssetAtPath<Material>(BeaconLightMatPath);

        var root = new GameObject("Sprinkler");
        root.layer = LayerMask.NameToLayer("Hitbox");
        var trigger = root.AddComponent<SphereCollider>();
        trigger.isTrigger = true;
        trigger.center = new Vector3(0f, 0.2f, 0f);
        trigger.radius = 0.35f;

        Part(PrimitiveType.Cylinder, "Base", root.transform, new Vector3(0f, 0.03f, 0f), new Vector3(0.45f, 0.03f, 0.45f), body);
        Part(PrimitiveType.Cylinder, "Post", root.transform, new Vector3(0f, 0.15f, 0f), new Vector3(0.1f, 0.12f, 0.1f), body);
        var head = new GameObject("Head").transform;
        head.SetParent(root.transform, false);
        head.localPosition = new Vector3(0f, 0.3f, 0f);
        Renderer hub = Part(PrimitiveType.Cylinder, "Hub", head, Vector3.zero, new Vector3(0.22f, 0.05f, 0.22f), light);
        Renderer arm = Part(PrimitiveType.Cube, "Arms", head, Vector3.zero, new Vector3(0.07f, 0.07f, 0.5f), light);
        Renderer tipA = Part(PrimitiveType.Sphere, "NozzleA", head, new Vector3(0f, 0f, 0.25f), Vector3.one * 0.11f, body);
        Renderer tipB = Part(PrimitiveType.Sphere, "NozzleB", head, new Vector3(0f, 0f, -0.25f), Vector3.one * 0.11f, body);

        Sprinkler sprinkler = root.AddComponent<Sprinkler>();
        var so = new SerializedObject(sprinkler);
        so.FindProperty("maxHealth").floatValue = 30f;
        so.FindProperty("lifetime").floatValue = 8f;
        SerializedProperty tinted = so.FindProperty("teamTinted");
        Renderer[] parts = { hub, arm };
        tinted.arraySize = parts.Length;
        for (int i = 0; i < parts.Length; i++) tinted.GetArrayElementAtIndex(i).objectReferenceValue = parts[i];
        so.FindProperty("head").objectReferenceValue = head;
        so.FindProperty("projectilePrefab").objectReferenceValue = AssetDatabase.LoadAssetAtPath<GameObject>(ProjectilePath);
        so.ApplyModifiedPropertiesWithoutUndo();

        GameObject saved = PrefabUtility.SaveAsPrefabAsset(root, SprinklerPath);
        Object.DestroyImmediate(root);
        return saved.GetComponent<Sprinkler>();
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

    // Adds PlayerLoadout (subs, weapon prefabs, mount) and the bubble shield to the player prefab.
    // The gun that used to be built into the player becomes the Airspray SE prefab.
    static void AddToPlayer(Beacon beaconPrefab, Sprinkler sprinklerPrefab, Material bubbleMat)
    {
        GameObject contents = PrefabUtility.LoadPrefabContents(PlayerPath);
        try
        {
            PlayerController pc = contents.GetComponentInChildren<PlayerController>(true);
            PlayerLoadout loadout = pc.GetComponent<PlayerLoadout>();
            if (loadout == null) loadout = pc.gameObject.AddComponent<PlayerLoadout>();

            Transform mount = pc.transform.Find("ViewmodelPlayer/WeaponPoint");
            Weapon builtIn = mount.GetComponentInChildren<Weapon>(true);
            Weapon airspray = AssetDatabase.LoadAssetAtPath<Weapon>(AirsprayPath);
            if (airspray == null) airspray = CreateAirspray(builtIn.transform);
            Weapon inkshot = AssetDatabase.LoadAssetAtPath<Weapon>(InkshotPath);
            if (inkshot == null) inkshot = CreateInkshot();
            foreach (Weapon w in mount.GetComponentsInChildren<Weapon>(true)) Object.DestroyImmediate(w.gameObject); // equipped at runtime now

            Transform bubble = pc.transform.Find("BubbleShield");
            if (bubble == null) bubble = CreateBubble(pc.transform, bubbleMat);

            var so = new SerializedObject(loadout);
            so.FindProperty("beaconPrefab").objectReferenceValue = beaconPrefab;
            so.FindProperty("sprinklerPrefab").objectReferenceValue = sprinklerPrefab;
            Weapon[] weapons = { airspray, inkshot };
            SerializedProperty list = so.FindProperty("weapons");
            list.arraySize = weapons.Length;
            for (int i = 0; i < weapons.Length; i++) list.GetArrayElementAtIndex(i).objectReferenceValue = weapons[i];
            so.FindProperty("weaponMount").objectReferenceValue = mount;
            so.FindProperty("shieldVisual").objectReferenceValue = bubble;
            so.ApplyModifiedPropertiesWithoutUndo();

            PrefabUtility.SaveAsPrefabAsset(contents, PlayerPath);
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(contents);
        }
    }

    static Transform CreateBubble(Transform player, Material bubbleMat)
    {
        GameObject bubble = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        Object.DestroyImmediate(bubble.GetComponent<Collider>());
        bubble.name = "BubbleShield";
        bubble.transform.SetParent(player, false);
        bubble.transform.localPosition = player.Find("ViewmodelPlayer").localPosition; // around the model, not the feet
        bubble.transform.localScale = Vector3.one * 2.6f;
        Renderer r = bubble.GetComponent<Renderer>();
        r.sharedMaterial = bubbleMat;
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        r.receiveShadows = false;
        bubble.SetActive(false);
        return bubble.transform;
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

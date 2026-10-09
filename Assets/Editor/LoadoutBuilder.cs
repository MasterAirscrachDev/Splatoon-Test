using System.IO;
using UnityEditor;
using UnityEngine;

// Builds the loadout assets: the weapon prefabs (Assets/Prefabs/Weapons), the Beacon and
// Sprinkler prefabs, the bubble shield, and the PlayerLoadout on the player prefab. Prefabs and
// the bubble are only created when missing, so tuning done on them survives a re-run.
public static class LoadoutBuilder
{
    const string BeaconPath = "Assets/Prefabs/Beacon.prefab";
    const string ColumnMeshPath = "Assets/Models/InkStrike.fbx";       // the InkStrike's central column
    const string ColumnMaterialPath = "Assets/Materials/InkShape.mat";
    const string SprinklerPath = "Assets/Prefabs/Sprinkler.prefab";
    const string CurlingBombPath = "Assets/Prefabs/CurlingBomb.prefab";
    const string BlasterPath = "Assets/Prefabs/Weapons/SolarBlaster.prefab";
    const string InkBlastPath = "Assets/Prefabs/InkBlast.prefab";
    const string RollerPath = "Assets/Prefabs/Weapons/Inkroller.prefab";
    const string RollerModelPath = "Assets/Models/Inkroller.fbx";
    const string BlastMatPath = "Assets/Materials/SwimWakeLit.mat"; // the curling bomb's blast: lit ink
    const string InkStrikePath = "Assets/Prefabs/InkStrike.prefab";
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
        CurlingBomb curling = AssetDatabase.LoadAssetAtPath<CurlingBomb>(CurlingBombPath);
        if (curling == null) curling = BuildCurlingBomb();
        Material bubble = EnsureMaterial(BubbleMatPath, "Ink/Bubble", null);
        InkStrike strike = AssetDatabase.LoadAssetAtPath<InkStrike>(InkStrikePath);
        if (strike == null) strike = BuildInkStrike(bubble);
        AddToPlayer(beacon, sprinkler, curling, strike, bubble);
        AssetDatabase.SaveAssets();
        Debug.Log($"[Loadout] Loadout assets ready; updated {PlayerPath}");
    }

    // A see-through column marking the area, and the ink tornado: a swirl filling the column plus a
    // spray off the ground, both drawn like the projectile splashes (ink spheres, vertex colour).
    // InkStrike fits their shapes to its radius and height when it starts.
    static InkStrike BuildInkStrike(Material shell)
    {
        var root = new GameObject("InkStrike");
        Renderer marker = Part(PrimitiveType.Cylinder, "Marker", root.transform, Vector3.zero, Vector3.one, shell);
        marker.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

        var projectile = AssetDatabase.LoadAssetAtPath<ProjectileVisual>(ProjectilePath);
        var splash = new SerializedObject(projectile).FindProperty("splashParticlesPrefab").objectReferenceValue as GameObject;
        ParticleSystemRenderer look = splash.GetComponentInChildren<ParticleSystemRenderer>(true);

        ParticleSystem tornado = InkParticleSystem("Tornado", root.transform, look);
        var main = tornado.main;
        main.duration = 1.4f;
        main.startLifetime = new ParticleSystem.MinMaxCurve(0.7f, 1.3f);
        main.startSpeed = 0f;
        main.startSize = new ParticleSystem.MinMaxCurve(0.35f, 0.9f);
        main.maxParticles = 4000;
        var emission = tornado.emission;
        emission.rateOverTime = 180f; // around the column mesh, not making it
        var shape = tornado.shape;
        shape.shapeType = ParticleSystemShapeType.ConeVolume; // angle 0: a cylinder, filled
        shape.angle = 0f;
        shape.rotation = new Vector3(-90f, 0f, 0f);           // along +Y
        var swirl = tornado.velocityOverLifetime;
        swirl.enabled = true;
        swirl.space = ParticleSystemSimulationSpace.Local;
        swirl.x = swirl.z = 0f;
        swirl.y = 3f;        // rising
        swirl.orbitalY = 4f; // spinning about the column
        swirl.radial = 0f;   // InkStrike sets its width
        var grow = tornado.sizeOverLifetime;
        grow.enabled = true;
        grow.size = new ParticleSystem.MinMaxCurve(1f, new AnimationCurve(new Keyframe(0f, 0f), new Keyframe(0.2f, 1f), new Keyframe(1f, 0f)));

        ParticleSystem burst = InkParticleSystem("Burst", root.transform, look);
        main = burst.main;
        main.duration = 0.3f;
        main.startLifetime = new ParticleSystem.MinMaxCurve(0.6f, 1.1f);
        main.startSpeed = new ParticleSystem.MinMaxCurve(6f, 13f);
        main.startSize = new ParticleSystem.MinMaxCurve(0.4f, 1f);
        main.gravityModifier = 1.5f;
        main.maxParticles = 600;
        emission = burst.emission;
        emission.rateOverTime = 0f;
        emission.SetBursts(new[] { new ParticleSystem.Burst(0f, 220) });
        shape = burst.shape;
        shape.shapeType = ParticleSystemShapeType.Cone;        // up off a disc, spreading a little
        shape.angle = 25f;
        shape.rotation = new Vector3(-90f, 0f, 0f);
        grow = burst.sizeOverLifetime;
        grow.enabled = true;
        grow.size = new ParticleSystem.MinMaxCurve(1f, new AnimationCurve(new Keyframe(0f, 1f), new Keyframe(1f, 0.2f)));

        InkStrike strike = root.AddComponent<InkStrike>();
        var so = new SerializedObject(strike);
        so.FindProperty("marker").objectReferenceValue = marker.transform;
        so.FindProperty("tornado").objectReferenceValue = tornado;
        Mesh columnMesh = AssetDatabase.LoadAssetAtPath<Mesh>(ColumnMeshPath);
        if (columnMesh != null)
        {
            var column = new GameObject("InkStrikeMesh");
            column.transform.SetParent(root.transform, false);
            column.AddComponent<MeshFilter>().sharedMesh = columnMesh;
            var columnRenderer = column.AddComponent<MeshRenderer>();
            columnRenderer.sharedMaterial = AssetDatabase.LoadAssetAtPath<Material>(ColumnMaterialPath);
            columnRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            so.FindProperty("column").objectReferenceValue = column.transform;
        }
        so.FindProperty("burst").objectReferenceValue = burst;
        so.ApplyModifiedPropertiesWithoutUndo();

        GameObject saved = PrefabUtility.SaveAsPrefabAsset(root, InkStrikePath);
        Object.DestroyImmediate(root);
        return saved.GetComponent<InkStrike>();
    }

    static ParticleSystem InkParticleSystem(string name, Transform parent, ParticleSystemRenderer look)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        ParticleSystem ps = go.AddComponent<ParticleSystem>();
        ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        var main = ps.main;
        main.loop = false;
        main.playOnAwake = false;
        main.simulationSpace = ParticleSystemSimulationSpace.Local;
        var r = go.GetComponent<ParticleSystemRenderer>();
        r.renderMode = look.renderMode;
        r.mesh = look.mesh;
        r.sharedMaterial = look.sharedMaterial;
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        return ps;
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
        so.FindProperty("shotSpeed").floatValue = 10f;
        so.FindProperty("fillerShots").intValue = 5;
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
            so.FindProperty("shotSpeed").floatValue = 18.75f;
            so.FindProperty("fillerShots").intValue = 7;
            // 1.25x as fast as a plain throw at 15 m/s, on the same path: gravity x1.25^2.
            so.FindProperty("ballistics.gravityOverTime").animationCurveValue = AnimationCurve.Constant(0f, 1f, 1.5625f);
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
        so.FindProperty("inkCost").floatValue = 0.6f;
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

    // Blaster: Airspray SE's set-up with a short, fat barrel, firing one exploding shot.
    internal static Weapon CreateBlaster()
    {
        GameObject c = PrefabUtility.LoadPrefabContents(AirsprayPath);
        try
        {
            c.name = "Solar Blaster";
            Transform muzzle = c.transform.Find("BulletSpawn");
            Object.DestroyImmediate(c.GetComponent<WeaponShooter>());
            Blaster blaster = c.AddComponent<Blaster>();
            var so = new SerializedObject(blaster);
            so.FindProperty("displayName").stringValue = "Solar Blaster";
            so.FindProperty("description").stringValue = "Slow, short-ranged shots that explode at the end of their flight or on whatever they hit.";
            so.FindProperty("muzzle").objectReferenceValue = muzzle;
            so.FindProperty("projectile").objectReferenceValue = AssetDatabase.LoadAssetAtPath<GameObject>(ProjectilePath);
            GameObject effect = AssetDatabase.LoadAssetAtPath<GameObject>(InkBlastPath);
            if (effect == null) effect = BuildInkBlast();
            so.FindProperty("blast.effect").objectReferenceValue = effect;
            so.ApplyModifiedPropertiesWithoutUndo();
            Transform model = c.transform.Find("InkShot");
            if (model != null) model.localScale = Vector3.Scale(model.localScale, new Vector3(1.5f, 1.5f, 0.75f));
            PrefabUtility.SaveAsPrefabAsset(c, BlasterPath);
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(c);
        }
        return AssetDatabase.LoadAssetAtPath<Weapon>(BlasterPath);
    }

    // The roller, around its model: the Inkroller set up in the open scene if there is one (it carries
    // the materials), else the bare model. Its drum is RollerFrame/Roller; its width the drum's.
    internal static Weapon CreateRoller()
    {
        GameObject source = GameObject.Find("Inkroller");
        if (source == null) source = AssetDatabase.LoadAssetAtPath<GameObject>(RollerModelPath);
        if (source == null) { Debug.LogError("[LoadoutBuilder] no Inkroller model"); return null; }
        var root = new GameObject("Inkroller");
        GameObject model = Object.Instantiate(source, root.transform);
        model.name = "Model";
        model.transform.localPosition = Vector3.zero;
        model.transform.localRotation = Quaternion.identity;
        model.transform.localScale = Vector3.one;
        foreach (Collider c in model.GetComponentsInChildren<Collider>(true)) Object.DestroyImmediate(c);
        foreach (Renderer r in model.GetComponentsInChildren<Renderer>(true)) r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
        Transform drum = model.transform.Find("RollerFrame/Roller");

        Roller roller = root.AddComponent<Roller>();
        var so = new SerializedObject(roller);
        so.FindProperty("displayName").stringValue = "Inkroller";
        so.FindProperty("description").stringValue = "Flick ink in a wide arc (or a long line from the air), then hold to roll a strip of ink and run enemies over.";
        so.FindProperty("projectile").objectReferenceValue = AssetDatabase.LoadAssetAtPath<GameObject>(ProjectilePath);
        so.FindProperty("roller").objectReferenceValue = drum;
        if (drum != null && drum.TryGetComponent(out MeshFilter mf) && mf.sharedMesh != null)
        {
            Vector3 size = Vector3.Scale(mf.sharedMesh.bounds.size, drum.lossyScale);
            so.FindProperty("rollerWidth").floatValue = size.y;            // the drum's axis is its mesh's Y
            so.FindProperty("rollerRadius").floatValue = size.x * 0.5f;
        }
        so.ApplyModifiedPropertiesWithoutUndo();
        PrefabUtility.SaveAsPrefabAsset(root, RollerPath);
        Object.DestroyImmediate(root);
        return AssetDatabase.LoadAssetAtPath<Weapon>(RollerPath);
    }

    // An explosion: a sphere of lit ink that swells and collapses, flinging droplets, and a splash.
    internal static GameObject BuildInkBlast()
    {
        var root = new GameObject("InkBlast");
        Renderer sphere = Part(PrimitiveType.Sphere, "Sphere", root.transform, Vector3.zero, Vector3.one, AssetDatabase.LoadAssetAtPath<Material>(BlastMatPath));
        sphere.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        InkBlastEffect effect = root.AddComponent<InkBlastEffect>();
        var so = new SerializedObject(effect);
        so.FindProperty("sphere").objectReferenceValue = sphere.transform;
        var projectile = AssetDatabase.LoadAssetAtPath<ProjectileVisual>(ProjectilePath);
        if (projectile != null) so.FindProperty("splash").objectReferenceValue = projectile.SplashParticles;
        so.ApplyModifiedPropertiesWithoutUndo();
        GameObject saved = PrefabUtility.SaveAsPrefabAsset(root, InkBlastPath);
        Object.DestroyImmediate(root);
        return saved;
    }

    // A squat puck with a grip on top and a light that blinks faster as its fuse runs out; it goes
    // off with the shared InkBlast effect. No collider: it can't be shot.
    internal static CurlingBomb BuildCurlingBomb()
    {
        Material body = EnsureMaterial(BeaconBodyMatPath, "Standard", m => m.color = new Color(0.22f, 0.22f, 0.26f));
        Material light = AssetDatabase.LoadAssetAtPath<Material>(BeaconLightMatPath);

        var root = new GameObject("CurlingBomb");
        var model = new GameObject("Body").transform;
        model.SetParent(root.transform, false);
        Part(PrimitiveType.Cylinder, "Puck", model, new Vector3(0f, 0.12f, 0f), new Vector3(0.6f, 0.11f, 0.6f), body);
        Renderer band = Part(PrimitiveType.Cylinder, "Band", model, new Vector3(0f, 0.12f, 0f), new Vector3(0.63f, 0.04f, 0.63f), light);
        Part(PrimitiveType.Cube, "Grip", model, new Vector3(0f, 0.3f, 0f), new Vector3(0.08f, 0.1f, 0.34f), body);
        Renderer blinker = Part(PrimitiveType.Sphere, "Light", model, new Vector3(0f, 0.25f, 0.16f), Vector3.one * 0.1f, light);


        CurlingBomb bomb = root.AddComponent<CurlingBomb>();
        var so = new SerializedObject(bomb);
        so.FindProperty("inkCost").floatValue = 0.7f;
        SerializedProperty tinted = so.FindProperty("teamTinted");
        tinted.arraySize = 1;
        tinted.GetArrayElementAtIndex(0).objectReferenceValue = band;
        so.FindProperty("blinker").objectReferenceValue = blinker;
        so.FindProperty("body").objectReferenceValue = model;
        GameObject effect = AssetDatabase.LoadAssetAtPath<GameObject>(InkBlastPath);
        if (effect == null) effect = BuildInkBlast();
        so.FindProperty("blastEffect").objectReferenceValue = effect;
        so.ApplyModifiedPropertiesWithoutUndo();

        GameObject saved = PrefabUtility.SaveAsPrefabAsset(root, CurlingBombPath);
        Object.DestroyImmediate(root);
        return saved.GetComponent<CurlingBomb>();
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
        so.FindProperty("inkCost").floatValue = 0.7f;
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
    static void AddToPlayer(Beacon beaconPrefab, Sprinkler sprinklerPrefab, CurlingBomb curlingBombPrefab, InkStrike inkStrikePrefab, Material bubbleMat)
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
            Weapon blaster = AssetDatabase.LoadAssetAtPath<Weapon>(BlasterPath);
            if (blaster == null) blaster = CreateBlaster();
            Weapon roller = AssetDatabase.LoadAssetAtPath<Weapon>(RollerPath);
            if (roller == null) roller = CreateRoller();
            foreach (Weapon w in mount.GetComponentsInChildren<Weapon>(true)) Object.DestroyImmediate(w.gameObject); // equipped at runtime now

            Transform bubble = pc.transform.Find("BubbleShield");
            if (bubble == null) bubble = CreateBubble(pc.transform, bubbleMat);

            var so = new SerializedObject(loadout);
            so.FindProperty("beaconPrefab").objectReferenceValue = beaconPrefab;
            so.FindProperty("sprinklerPrefab").objectReferenceValue = sprinklerPrefab;
            so.FindProperty("curlingBombPrefab").objectReferenceValue = curlingBombPrefab;
            so.FindProperty("inkStrikePrefab").objectReferenceValue = inkStrikePrefab;
            Weapon[] weapons = { airspray, inkshot, blaster, roller };
            SerializedProperty list = so.FindProperty("weapons");
            list.arraySize = weapons.Length;
            for (int i = 0; i < weapons.Length; i++) list.GetArrayElementAtIndex(i).objectReferenceValue = weapons[i];
            so.FindProperty("weaponMount").objectReferenceValue = mount;
            so.FindProperty("shieldVisual").objectReferenceValue = bubble;
            Transform subLight = pc.transform.Find("ViewmodelPlayer/InkTank/Sub Light");
            if (subLight != null) so.FindProperty("subReadyLight").objectReferenceValue = subLight.GetComponent<Light>();
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

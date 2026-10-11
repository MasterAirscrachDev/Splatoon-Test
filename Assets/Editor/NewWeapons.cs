using System.Linq;
using UnityEditor;
using UnityEngine;

// The Heavy Roller and the Inkbrush (create-only: an existing prefab is left as it is), added to the
// player's loadout, and every weapon's loadout-card ratings (Tools/Weapons/Set Loadout Ratings).
//
// Heavy Roller: the Inkroller retuned round W_HeavyRoller (scaled up to roll a wider strip): slower
// flicks and rolling, but further, wider, harder-hitting flicks and a heavier roll.
// Inkbrush: a Brush round W_Brush: quick alternating swipes and a fast, narrow, light brush.
public static class NewWeapons
{
    const string Prefabs = "Assets/Prefabs/Weapons/", Models = "Assets/Models/Weapons/";
    const string PlayerPrefab = "Assets/Prefabs/PlayerEntity - VOID.prefab";
    const float HeavyScale = 1.25f;                       // the model's drum 2.46 x 1.25: wider than the Inkroller's 2.62

    // Rough 0-1 power / range / speed for the loadout card's bars (by eye from each weapon's tuning).
    static readonly (string prefab, float power, float range, float speed)[] Ratings =
    {
        ("AirspraySE",     0.30f, 0.35f, 0.90f),
        ("Inkshot",        0.50f, 0.60f, 0.60f),
        ("SolarBlaster",   0.85f, 0.45f, 0.30f),
        ("ArtemisBlaster", 0.45f, 0.70f, 0.50f),
        ("Inkroller",      0.75f, 0.35f, 0.50f),
        ("EraRoller",      0.30f, 0.35f, 0.80f),
        ("HeavyRoller",    0.95f, 0.75f, 0.20f),
        ("Inkbrush",       0.25f, 0.20f, 0.95f),
    };

    [MenuItem("Tools/Weapons/Create Heavy Roller And Inkbrush")]
    public static void CreateMenu() => Debug.Log(Create());

    public static string Create()
    {
        string log = WeaponModels.ExtractMaterials(Models + "W_HeavyRoller.fbx") + "; " + WeaponModels.ExtractMaterials(Models + "W_Brush.fbx");
        if (!System.IO.File.Exists(Prefabs + "HeavyRoller.prefab")) { CreateHeavyRoller(); log += "; made HeavyRoller"; }
        if (!System.IO.File.Exists(Prefabs + "Inkbrush.prefab")) { CreateInkbrush(); log += "; made Inkbrush"; }
        log += "; " + AddToLoadout("HeavyRoller", "Inkbrush");
        log += "; " + SetRatings();
        AssetDatabase.SaveAssets();
        return "[NewWeapons] " + log;
    }

    static void CreateHeavyRoller()
    {
        GameObject root = PrefabUtility.LoadPrefabContents(Prefabs + "Inkroller.prefab");
        try
        {
            root.name = "HeavyRoller";
            Object.DestroyImmediate(root.transform.Find("Model").gameObject);
            var model = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(Models + "W_HeavyRoller.fbx"), root.transform);
            model.name = "Model";
            model.transform.localPosition = Vector3.zero;          // keeps its own 90 degree turn (its pieces are in Blender's axes)
            model.transform.localScale = Vector3.one * HeavyScale;
            Transform drum = model.GetComponentsInChildren<Transform>(true).First(t => t.name == "HeavyRoller_Drum");

            var so = new SerializedObject(root.GetComponent<Roller>());
            so.FindProperty("displayName").stringValue = "Heavy Roller";
            so.FindProperty("description").stringValue = "A heavy roller, slow to swing. Its ground flick throws a wide wall of ink a long way and hits hard. Its roll starts slow and builds to walking pace over a few seconds, inking a strip wider than the roller itself.";
            so.FindProperty("icon").objectReferenceValue = null;
            so.FindProperty("roller").objectReferenceValue = drum;
            so.FindProperty("spinAxis").vector3Value = Vector3.left;
            so.FindProperty("turnAxis").vector3Value = Vector3.back;
            so.FindProperty("rollerWidth").floatValue = 2.46f * HeavyScale;
            so.FindProperty("rollerRadius").floatValue = 0.213f * HeavyScale;
            HeavyTuning(so);
            so.ApplyModifiedPropertiesWithoutUndo();
            PrefabUtility.SaveAsPrefabAsset(root, Prefabs + "HeavyRoller.prefab");
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }
    }

    // Slower swings than the Inkroller; a ground flick that throws further and harder, an air flick
    // that doesn't go quite as far (and 80% of its first damage); a slow roll that builds up to walking
    // speed over 3s, inking past the drum's ends.
    public static void HeavyTuning(SerializedObject so)
    {
        so.FindProperty("flickRate").floatValue = 1.0f;
        so.FindProperty("windup").floatValue = 0.34f;
        so.FindProperty("followThrough").floatValue = 0.24f;
        so.FindProperty("flickInkCost").floatValue = 160f;
        so.FindProperty("groundDroplets").intValue = 16;
        so.FindProperty("groundFan").floatValue = 100f;
        so.FindProperty("groundSpeed").vector2Value = new Vector2(8f, 12.5f);
        so.FindProperty("groundDamage.damage").floatValue = 230f;
        so.FindProperty("groundDamage.scaleOverTime").animationCurveValue = DamageFalloff.HoldThenFall(0.1f, 0.6f, 0.45f);
        so.FindProperty("groundSplat").intValue = 20;
        so.FindProperty("airDroplets").intValue = 16;
        so.FindProperty("airSpeed").vector2Value = new Vector2(0f, 13f);
        so.FindProperty("airDamage.damage").floatValue = 192f;
        so.FindProperty("airSplat").intValue = 15;
        so.FindProperty("rollDamage").floatValue = 150f;
        so.FindProperty("rollStartSpeed").floatValue = 0.35f;
        so.FindProperty("rollSpeed").floatValue = 1.0f;
        so.FindProperty("rollRampTime").floatValue = 3f;
        so.FindProperty("rollInkPerMetre").floatValue = 24f;
        so.FindProperty("rollInkWidth").floatValue = 4.2f;
        so.FindProperty("rollSplatPoints").intValue = 5;
        so.FindProperty("rollSplatSize").intValue = 11;
    }

    [MenuItem("Tools/Weapons/Retune Heavy Roller")]
    public static void RetuneHeavy()
    {
        var roller = AssetDatabase.LoadAssetAtPath<Roller>(Prefabs + "HeavyRoller.prefab");
        var so = new SerializedObject(roller);
        HeavyTuning(so);
        so.ApplyModifiedPropertiesWithoutUndo();
        EditorUtility.SetDirty(roller);
        AssetDatabase.SaveAssets();
    }

    static void CreateInkbrush()
    {
        var root = new GameObject("Inkbrush");
        try
        {
            var model = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(Models + "W_Brush.fbx"), root.transform);
            model.name = "Model";
            model.transform.localPosition = Vector3.zero;
            model.transform.localRotation = Quaternion.Euler(0f, 180f, 0f); // exported facing -Z
            var head = new GameObject("Head").transform;                     // the bristles' middle
            head.SetParent(root.transform, false);
            head.localPosition = new Vector3(0f, -0.16f, 1.55f);

            Brush brush = root.AddComponent<Brush>();
            var so = new SerializedObject(brush);
            var roller = new SerializedObject(AssetDatabase.LoadAssetAtPath<Roller>(Prefabs + "Inkroller.prefab"));
            so.FindProperty("displayName").stringValue = "Inkbrush";
            so.FindProperty("description").stringValue = "Quick swipes from side to side, wherever you look (each the other way from the last), then hold to brush a strip along the floor, much faster than a roller but for little damage.";
            so.FindProperty("projectile").objectReferenceValue = roller.FindProperty("projectile").objectReferenceValue;
            so.CopyFromSerializedProperty(roller.FindProperty("ballistics"));
            so.FindProperty("head").objectReferenceValue = head;
            so.ApplyModifiedPropertiesWithoutUndo();
            PrefabUtility.SaveAsPrefabAsset(root, Prefabs + "Inkbrush.prefab");
        }
        finally
        {
            Object.DestroyImmediate(root);
        }
    }

    // On the end of the player's weapon list (the synced index of the ones before doesn't change).
    static string AddToLoadout(params string[] names)
    {
        GameObject player = PrefabUtility.LoadPrefabContents(PlayerPrefab);
        int added = 0;
        try
        {
            var so = new SerializedObject(player.GetComponentInChildren<PlayerLoadout>(true));
            SerializedProperty list = so.FindProperty("weapons");
            foreach (string name in names)
            {
                var weapon = AssetDatabase.LoadAssetAtPath<Weapon>(Prefabs + name + ".prefab");
                bool has = false;
                for (int i = 0; i < list.arraySize; i++) has |= list.GetArrayElementAtIndex(i).objectReferenceValue == weapon;
                if (has || weapon == null) continue;
                list.arraySize++;
                list.GetArrayElementAtIndex(list.arraySize - 1).objectReferenceValue = weapon;
                added++;
            }
            so.ApplyModifiedPropertiesWithoutUndo();
            PrefabUtility.SaveAsPrefabAsset(player, PlayerPrefab);
            return $"loadout +{added} ({list.arraySize} weapons)";
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(player);
        }
    }

    [MenuItem("Tools/Weapons/Set Loadout Ratings")]
    public static void SetRatingsMenu() => Debug.Log(SetRatings());

    public static string SetRatings()
    {
        int set = 0;
        foreach (var (prefab, power, range, speed) in Ratings)
        {
            var weapon = AssetDatabase.LoadAssetAtPath<Weapon>(Prefabs + prefab + ".prefab");
            if (weapon == null) continue;
            var so = new SerializedObject(weapon);
            so.FindProperty("powerRating").floatValue = power;
            so.FindProperty("rangeRating").floatValue = range;
            so.FindProperty("speedRating").floatValue = speed;
            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(weapon);
            set++;
        }
        return $"ratings on {set} weapons";
    }
}

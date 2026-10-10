using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

// Puts the weapon models exported from Maps.blend's "Weapons" scene (Assets/Models/Weapons/W_*.fbx)
// into the weapon prefabs, in place of the old models and placeholder primitives. The models go in as
// linked instances named "Model", so re-exports from Blender come straight through.
//
// Their materials are extracted to Assets/Materials/Weapons (one per name, shared between models;
// an existing Assets/Materials one by that name is used instead), so reskins can swap them.
//
// The static models were exported with their axes baked, facing -Z: they're turned round. The rollers
// keep the root's 90 degree turn Unity gives them (their pieces are in Blender's axes beneath it), so the
// Roller is told its drum's axle and its frame's turn axis in those axes.
public static class WeaponModels
{
    const string Models = "Assets/Models/Weapons/", Prefabs = "Assets/Prefabs/Weapons/", MaterialDir = "Assets/Materials/Weapons";

    // weapon prefab -> model, BulletSpawn (the muzzle: Blender's muzzle x the model's 1.662 scale), material swaps (reskins)
    static readonly (string prefab, string model, Vector3? muzzle, (string slot, string material)[] swaps)[] Weapons =
    {
        ("AirspraySE",     "W_Airspray", null, new (string, string)[0]),
        ("Inkroller",      "W_Roller",   null, new (string, string)[0]),
        ("EraRoller",      "W_Roller",   null, new[] { ("Frame", "Assets/Materials/EraRoller.mat") }),
        ("Inkshot",        "W_Inkshot",  new Vector3(0f, 0.1325f * 1.662f, 0.461f * 1.662f), new (string, string)[0]),
        ("SolarBlaster",   "W_Blaster",  new Vector3(0f, 0.2025f * 1.662f, 0.61f * 1.662f), new (string, string)[0]),
        ("ArtemisBlaster", "W_Blaster",  new Vector3(0f, 0.2025f * 1.662f, 0.61f * 1.662f),
            new[] { ("BlasterBody", "Assets/Materials/BeaconBody.mat"), ("BlasterTrim", MaterialDir + "/BlasterTrim Artemis.mat") }),
    };
    static readonly string[] OldModels = { "Assets/Models/Weapons/InkShot.fbx", "Assets/Models/Weapons/Inkroller.fbx" };

    [MenuItem("Tools/Weapons/Swap In New Models")]
    public static void SwapInNewModels() => Debug.Log(Swap());

    public static string Swap()
    {
        var log = new List<string>();
        foreach (string model in Weapons.Select(w => w.model).Distinct()) log.Add(ExtractMaterials(Models + model + ".fbx"));
        EnsureArtemisTrim();
        foreach (var w in Weapons) log.Add(Refit(w.prefab, w.model, w.muzzle, w.swaps));
        AssetDatabase.SaveAssets();
        // The old models go once nothing uses them.
        foreach (string old in OldModels)
        {
            if (!File.Exists(old)) continue;
            string guid = AssetDatabase.AssetPathToGUID(old);
            var users = AssetDatabase.FindAssets("t:Prefab t:Scene", new[] { "Assets" }).Select(AssetDatabase.GUIDToAssetPath)
                .Where(p => File.ReadAllText(p).Contains(guid)).ToList();
            if (users.Count > 0) { log.Add($"kept {old}: still used by {string.Join(", ", users)}"); continue; }
            AssetDatabase.DeleteAsset(old);
            log.Add($"deleted {old}");
        }
        return "[WeaponModels] " + string.Join("; ", log);
    }

    // Each embedded material out to Assets/Materials/Weapons (or onto the existing one of that name).
    static string ExtractMaterials(string modelPath)
    {
        Directory.CreateDirectory(MaterialDir);
        var importer = (ModelImporter)AssetImporter.GetAtPath(modelPath);
        var done = new List<string>();
        foreach (Material embedded in AssetDatabase.LoadAllAssetsAtPath(modelPath).OfType<Material>().ToList())
        {
            string name = embedded.name;
            string shared = $"Assets/Materials/{name}.mat", own = $"{MaterialDir}/{name}.mat";
            Material target = Named(shared, name) ?? Named(own, name);
            if (target == null)
            {
                string error = AssetDatabase.ExtractAsset(embedded, own);
                if (!string.IsNullOrEmpty(error)) { done.Add($"{name}: {error}"); continue; }
                AssetDatabase.ImportAsset(own);
                target = AssetDatabase.LoadAssetAtPath<Material>(own);
            }
            importer.AddRemap(new AssetImporter.SourceAssetIdentifier(typeof(Material), name), target);
            done.Add($"{name}->{AssetDatabase.GetAssetPath(target)}");
        }
        importer.SaveAndReimport();
        return $"{Path.GetFileName(modelPath)} materials: {string.Join(", ", done)}";
    }

    // The material at a path, if its name matches exactly (paths ignore case on Windows: "Inktank" isn't "InkTank").
    static Material Named(string path, string name)
    {
        var m = AssetDatabase.LoadAssetAtPath<Material>(path);
        return m != null && m.name == name ? m : null;
    }

    static void EnsureArtemisTrim()
    {
        string path = MaterialDir + "/BlasterTrim Artemis.mat";
        if (File.Exists(path)) return;
        var trim = AssetDatabase.LoadAssetAtPath<Material>(MaterialDir + "/BlasterTrim.mat");
        if (trim == null) return;
        var artemis = new Material(trim) { color = new Color(0.2f, 0.75f, 0.85f) }; // the Artemis: cyan where the Solar's orange
        AssetDatabase.CreateAsset(artemis, path);
    }

    static string Refit(string prefabName, string modelName, Vector3? muzzle, (string slot, string material)[] swaps)
    {
        string path = Prefabs + prefabName + ".prefab";
        var source = AssetDatabase.LoadAssetAtPath<GameObject>(Models + modelName + ".fbx");
        GameObject root = PrefabUtility.LoadPrefabContents(path);
        try
        {
            // Out with the old model or placeholder primitives (everything but the muzzle).
            foreach (Transform child in root.transform.Cast<Transform>().ToList())
                if (child.name != "BulletSpawn") Object.DestroyImmediate(child.gameObject);

            var model = (GameObject)PrefabUtility.InstantiatePrefab(source, root.transform);
            model.name = "Model";
            bool roller = root.TryGetComponent(out Roller _);
            model.transform.localPosition = Vector3.zero;                       // not its spot in the Blender row
            if (!roller) model.transform.localRotation = Quaternion.Euler(0f, 180f, 0f); // exported facing -Z
            model.transform.localScale = Vector3.one;

            foreach (var (slot, materialPath) in swaps)
            {
                var swap = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
                foreach (Renderer r in model.GetComponentsInChildren<Renderer>(true))
                    r.sharedMaterials = r.sharedMaterials.Select(m => m != null && m.name == slot && swap != null ? swap : m).ToArray();
            }

            var so = new SerializedObject(root.GetComponent<Weapon>());
            if (roller)
            {
                Transform drum = model.GetComponentsInChildren<Transform>(true).First(t => t.name == "Roller");
                so.FindProperty("roller").objectReferenceValue = drum;
                so.FindProperty("spinAxis").vector3Value = Vector3.left;   // its axle is X; turning as the old model did
                so.FindProperty("turnAxis").vector3Value = Vector3.back;   // the world's up in the frame's Blender axes
            }
            if (muzzle.HasValue)
            {
                Transform spawn = root.transform.Find("BulletSpawn");
                if (spawn != null) spawn.localPosition = muzzle.Value;
            }
            so.ApplyModifiedPropertiesWithoutUndo();
            PrefabUtility.SaveAsPrefabAsset(root, path);
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }
        return $"{prefabName} <- {modelName}";
    }
}

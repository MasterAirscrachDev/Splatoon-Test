using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

// Keeps Resources/MapCatalog in step with the build's scenes: a scene's entry is updated when it's
// saved, and the whole list when the build's scene list changes (or from Tools/Maps/Refresh Map Catalog).
[InitializeOnLoad]
public static class MapCatalogBuilder
{
    const string AssetPath = "Assets/Resources/MapCatalog.asset";

    static MapCatalogBuilder()
    {
        EditorSceneManager.sceneSaved += scene => { if (InBuild(scene.path)) { Record(Catalog(), scene); Save(); } };
        EditorBuildSettings.sceneListChanged += () => EditorApplication.delayCall += Refresh;
    }

    [MenuItem("Tools/Maps/Refresh Map Catalog")]
    public static void Refresh()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode) return;
        MapCatalog catalog = Catalog();
        catalog.maps.RemoveAll(m => !InBuild(m.scene));
        foreach (EditorBuildSettingsScene entry in EditorBuildSettings.scenes)
        {
            if (!File.Exists(entry.path)) continue;
            Scene scene = SceneManager.GetSceneByPath(entry.path);
            bool opened = !scene.isLoaded;
            if (opened) scene = EditorSceneManager.OpenScene(entry.path, OpenSceneMode.Additive);
            try { Record(catalog, scene); }
            finally { if (opened) EditorSceneManager.CloseScene(scene, true); }
        }
        Save();
        Debug.Log($"[MapCatalog] {catalog.maps.Count} maps: " + string.Join(", ", catalog.maps.ConvertAll(m => $"{Path.GetFileNameWithoutExtension(m.scene)} {MapCatalog.Describe(m.scene)}")));
    }

    static void Record(MapCatalog catalog, Scene scene)
    {
        MapSettings settings = null;
        foreach (GameObject root in scene.GetRootGameObjects())
            if (settings == null) settings = root.GetComponentInChildren<MapSettings>(true);
        var so = settings != null ? new SerializedObject(settings) : null;
        MapCatalog.Map map = catalog.maps.Find(m => m.scene == scene.path);
        if (map == null) catalog.maps.Add(map = new MapCatalog.Map { scene = scene.path });
        map.teams = so != null ? Mathf.Clamp(so.FindProperty("teamCount").intValue, 2, Teams.Max) : 2;
        map.matches = so == null || so.FindProperty("matchesAllowed").boolValue;
        EditorUtility.SetDirty(catalog);
    }

    static bool InBuild(string path)
    {
        foreach (EditorBuildSettingsScene s in EditorBuildSettings.scenes) if (s.path == path) return true;
        return false;
    }

    static MapCatalog Catalog()
    {
        var catalog = AssetDatabase.LoadAssetAtPath<MapCatalog>(AssetPath);
        if (catalog != null) return catalog;
        catalog = ScriptableObject.CreateInstance<MapCatalog>();
        AssetDatabase.CreateAsset(catalog, AssetPath);
        return catalog;
    }

    static void Save() => AssetDatabase.SaveAssets();
}

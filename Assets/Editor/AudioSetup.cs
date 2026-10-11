using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

// Music setup: the tracks' import settings, the library AudioManager and the boombox read
// (Resources/MusicLibrary), the boombox prefab, and the boombox in the Lobby.
public static class AudioSetup
{
    const string MusicFolder = "Assets/Audio/Music";
    const string LobbyTrack = MusicFolder + "/Decorum - Lobby.ogg";
    const string MatchTrack = MusicFolder + "/4Stones - Game.ogg";
    const string FinalMinuteTrack = MusicFolder + "/NowOrNever.ogg";
    const string LibraryPath = "Assets/Resources/MusicLibrary.asset";
    const string BoomboxModel = "Assets/Models/Props/Boombox.fbx";
    const string BoomboxPrefab = "Assets/Prefabs/Boombox.prefab";
    const string LobbyScene = "Assets/Scenes/Lobby.unity";

    [MenuItem("Tools/Audio/Set Up Music")]
    public static void SetUpMusic()
    {
        // Long tracks: streamed (not decoded into memory), 2D unless a source says otherwise.
        foreach (string guid in AssetDatabase.FindAssets("t:AudioClip", new[] { MusicFolder }))
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            var importer = (AudioImporter)AssetImporter.GetAtPath(path);
            AudioImporterSampleSettings s = importer.defaultSampleSettings;
            if (s.loadType == AudioClipLoadType.Streaming && s.compressionFormat == AudioCompressionFormat.Vorbis) continue;
            s.loadType = AudioClipLoadType.Streaming;
            s.compressionFormat = AudioCompressionFormat.Vorbis;
            s.quality = 0.7f;
            importer.defaultSampleSettings = s;
            importer.loadInBackground = true;
            importer.SaveAndReimport();
        }

        MusicLibrary library = AssetDatabase.LoadAssetAtPath<MusicLibrary>(LibraryPath);
        if (library == null)
        {
            library = ScriptableObject.CreateInstance<MusicLibrary>();
            AssetDatabase.CreateAsset(library, LibraryPath);
        }
        library.lobby = AssetDatabase.LoadAssetAtPath<AudioClip>(LobbyTrack);
        library.match = AssetDatabase.LoadAssetAtPath<AudioClip>(MatchTrack);
        library.finalMinute = AssetDatabase.LoadAssetAtPath<AudioClip>(FinalMinuteTrack);
        EditorUtility.SetDirty(library);
        AssetDatabase.SaveAssets();
        Debug.Log($"[Audio] Music library: lobby {Describe(library.lobby)}, match {Describe(library.match)}, final minute {Describe(library.finalMinute)}");
    }

    static string Describe(AudioClip clip) => clip != null ? $"{clip.name} ({clip.length:F0}s)" : "none";

    // The boombox: its model (cones pulse), a partly spatial music source and a box to stand on.
    [MenuItem("Tools/Audio/Build Boombox Prefab")]
    public static void BuildBoombox()
    {
        var modelImporter = (ModelImporter)AssetImporter.GetAtPath(BoomboxModel);
        if (!modelImporter.bakeAxisConversion)
        {
            modelImporter.bakeAxisConversion = true;
            modelImporter.SaveAndReimport();
        }
        GameObject model = AssetDatabase.LoadAssetAtPath<GameObject>(BoomboxModel);
        var root = new GameObject("Boombox");
        try
        {
            GameObject body = (GameObject)PrefabUtility.InstantiatePrefab(model, root.transform);
            body.name = "Model";
            Transform[] cones = body.GetComponentsInChildren<Transform>(true).Where(t => t.name.StartsWith("Boombox_Cone")).ToArray();

            Bounds bounds = new Bounds(root.transform.position, Vector3.zero);
            foreach (Renderer r in body.GetComponentsInChildren<Renderer>()) bounds.Encapsulate(r.bounds);
            BoxCollider box = root.AddComponent<BoxCollider>();
            box.center = bounds.center - root.transform.position;
            box.size = new Vector3(bounds.size.x, Mathf.Min(bounds.size.y, 0.62f), bounds.size.z); // the case, not the handle and aerial
            box.center = new Vector3(box.center.x, box.size.y / 2f, box.center.z);

            AudioSource source = root.AddComponent<AudioSource>();
            source.playOnAwake = false;
            source.loop = true;
            source.rolloffMode = AudioRolloffMode.Logarithmic;
            source.minDistance = 12f;
            source.maxDistance = 120f;
            source.dopplerLevel = 0f;

            Boombox boombox = root.AddComponent<Boombox>();
            var so = new SerializedObject(boombox);
            so.FindProperty("source").objectReferenceValue = source;
            SerializedProperty list = so.FindProperty("cones");
            list.arraySize = cones.Length;
            for (int i = 0; i < cones.Length; i++) list.GetArrayElementAtIndex(i).objectReferenceValue = cones[i];
            so.ApplyModifiedPropertiesWithoutUndo();

            PrefabUtility.SaveAsPrefabAsset(root, BoomboxPrefab);
            Debug.Log($"[Audio] Boombox prefab: {cones.Length} cones, case {box.size}, front {(cones.Length > 0 ? (cones[0].position - root.transform.position).ToString("F2") : "?")}");
        }
        finally
        {
            Object.DestroyImmediate(root);
        }
    }

    // On the platform in the middle of the Lobby, facing the spawn side.
    [MenuItem("Tools/Audio/Place Lobby Boombox")]
    public static void PlaceInLobby()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode) { Debug.LogError("[Audio] Not in play mode"); return; }
        var scene = EditorSceneManager.GetActiveScene();
        if (scene.path != LobbyScene) scene = EditorSceneManager.OpenScene(LobbyScene, OpenSceneMode.Single);
        foreach (Boombox old in Object.FindObjectsByType<Boombox>(FindObjectsInactive.Include, FindObjectsSortMode.None)) Object.DestroyImmediate(old.gameObject);
        Vector3 spot = new Vector3(-8f, 0f, 0f);
        if (Physics.Raycast(spot + Vector3.up * 30f, Vector3.down, out RaycastHit ground, 40f)) spot = ground.point;
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(BoomboxPrefab);
        var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene);
        go.transform.SetPositionAndRotation(spot, Quaternion.Euler(0f, 90f, 0f));
        go.transform.localScale = Vector3.one * 2.2f;
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        Debug.Log($"[Audio] Boombox placed in the Lobby at {spot}");
    }
}

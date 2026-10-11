using UnityEngine;
using UnityEngine.SceneManagement;

// The game's whole UI on one canvas (Assets/Prefabs/UI/GameUI.prefab), made once by NetGameManager
// and carried through every scene with it. Full-screen panels, drawn in this order: the HUD, the
// map, the loadout, lobby, pause and host menus. On top, one nested canvas (Live) for what moves
// every frame (the reticles, the swim ink tank, the menu cursor), so their changes don't rebuild
// the rest. A scene change closes any open menu and clears the kill feed.
public class GameUI : MonoBehaviour
{
    [SerializeField] RectTransform live; // the nested canvas for things that move every frame

    public static GameUI Instance { get; private set; }
    public RectTransform Live => live;

    void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
        SceneManager.sceneLoaded += OnSceneLoaded;
    }

    void OnDestroy()
    {
        SceneManager.sceneLoaded -= OnSceneLoaded;
        if (Instance == this) Instance = null;
    }

    void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        if (mode != LoadSceneMode.Single) return;
        PauseMenu pause = GetComponentInChildren<PauseMenu>(true);
        if (pause != null && pause.IsOpen) pause.SetOpen(false);
        HostMenu host = GetComponentInChildren<HostMenu>(true);
        if (host != null && host.IsOpen) host.SetOpen(false);
        LoadoutMenu loadout = GetComponentInChildren<LoadoutMenu>(true);
        if (loadout != null && loadout.IsOpen) loadout.SetOpen(false);
        LobbyMenu lobby = GetComponentInChildren<LobbyMenu>(true);
        if (lobby != null) lobby.Close();
        MapScreen map = GetComponentInChildren<MapScreen>(true);
        if (map != null && map.IsOpen) map.SetOpen(false);
        KillFeed feed = GetComponentInChildren<KillFeed>(true);
        if (feed != null) feed.Clear();
    }
}

using System.Collections.Concurrent;
using System.Collections.Generic;
using UnityEngine;
using Steamworks;

// Game manager: lobby, player spawning, team colours, scoring and network message routing.
// Steam callbacks arrive off the main thread, so handlers touching Unity objects enqueue to
// mainThread, drained in Update.
public class NetGameManager : MonoBehaviour
{
    public static NetGameManager Instance { get; private set; }
    public static PlayerController LocalPlayer { get; private set; }

    [Header("Spawning")]
    [SerializeField] GameObject playerEntityPrefab;
    [SerializeField] int maxPlayers = 8;
    [SerializeField] bool devMode;
    [SerializeField] bool autoLobby;

    [Header("Team Colours")]
    [SerializeField] Color alphaTeam = Color.cyan;
    [SerializeField] Color betaTeam  = Color.magenta;
    public Color AlphaTeam => alphaTeam;
    public Color BetaTeam  => betaTeam;

    const string LobbyTag = "TestSplatoongame";

    readonly Dictionary<ulong, PlayerController> players = new Dictionary<ulong, PlayerController>();
    readonly ConcurrentQueue<System.Action> mainThread = new ConcurrentQueue<System.Action>();

    SurfaceInkManager[] surfaceManagers;
    PlayerController localPlayer;
    ulong localId;
    SteamNetwork steamNet;
    ControlLayer input;

    void Awake()
    {
        Instance = this;

        var all = FindObjectsByType<SurfaceInkManager>(FindObjectsSortMode.None); // sorted by path so ids match on every client
        System.Array.Sort(all, (a, b) => HierarchyPath(a.transform).CompareTo(HierarchyPath(b.transform)));
        surfaceManagers = all;
        for (int i = 0; i < surfaceManagers.Length; i++)
            surfaceManagers[i].SurfaceId = i;

        if (autoLobby && !devMode)
        {
            steamNet = FindFirstObjectByType<SteamNetwork>();
            if (steamNet != null) steamNet.onSteamSetup += OnSteamReady;
        }
    }

    void Start()
    {
        input = new ControlLayer();
        input.Enable();
        input.GameControl.GameStart.performed += _ => OnGameStart();

        if (devMode)
        {
            localPlayer = InstantiateEntity(Vector3.zero, PlayerMode.Client, 1, LocalPlayerName());
            LocalPlayer = localPlayer;
            players[localId] = localPlayer;
        }
    }

    void OnDestroy()
    {
        if (Instance == this) { Instance = null; LocalPlayer = null; }
        if (steamNet != null) steamNet.onSteamSetup -= OnSteamReady;
        input?.Dispose();
    }

    void OnEnable()
    {
        SteamGlobal.OnLobbyUpdate += HandleLobbyUpdate;
        SteamGlobal.Bind((ushort)NetMsg.PlayerSpawn,   OnPlayerSpawnMsg);
        SteamGlobal.Bind((ushort)NetMsg.PlayerDespawn, OnPlayerDespawnMsg);
        SteamGlobal.Bind((ushort)NetMsg.PlayerState,   OnPlayerStateMsg);
        SteamGlobal.Bind((ushort)NetMsg.Splat,         OnSplatMsg);
        SteamGlobal.Bind((ushort)NetMsg.InkReset,      OnInkResetMsg);
        SteamGlobal.Bind((ushort)NetMsg.Teleport,      OnTeleportMsg);
        SurfaceInkManager.OnSplatApplied += OnLocalSplat;
    }

    void OnDisable()
    {
        SteamGlobal.OnLobbyUpdate -= HandleLobbyUpdate;
        SteamGlobal.UnBind((ushort)NetMsg.PlayerSpawn,   OnPlayerSpawnMsg);
        SteamGlobal.UnBind((ushort)NetMsg.PlayerDespawn, OnPlayerDespawnMsg);
        SteamGlobal.UnBind((ushort)NetMsg.PlayerState,   OnPlayerStateMsg);
        SteamGlobal.UnBind((ushort)NetMsg.Splat,         OnSplatMsg);
        SteamGlobal.UnBind((ushort)NetMsg.InkReset,      OnInkResetMsg);
        SteamGlobal.UnBind((ushort)NetMsg.Teleport,      OnTeleportMsg);
        SurfaceInkManager.OnSplatApplied -= OnLocalSplat;
    }

    // ── Auto-lobby ─────────────────────────────────────────────────────────
    async void OnSteamReady(bool success)
    {
        if (!success) return;

        var lobbies = await SteamGlobal.GetAllPublicLobbies(
            distance: SteamLobbySearchDistance.Worldwide,
            filters: new Dictionary<string, string> { { "game", LobbyTag } },
            requireOpenSlots: 1);

        if (lobbies != null && lobbies.Length > 0)
        {
            Debug.Log($"[AutoLobby] Joining existing lobby ({lobbies[0].Id})");
            await SteamGlobal.JoinLobbyFromID(lobbies[0].Id);
        }
        else
        {
            Debug.Log("[AutoLobby] No lobby found — hosting");
            await SteamGlobal.CreateLobby(maxPlayers, HostMode.Public,
                new Dictionary<string, string> { { "game", LobbyTag } });
        }
    }

    // ── Public lobby API (wire to UI buttons) ─────────────────────────────
    public async void HostLobby(HostMode mode = HostMode.Friends)
        => await SteamGlobal.CreateLobby(maxPlayers, mode);

    public async void JoinLobby(SteamId id)
        => await SteamGlobal.JoinLobbyFromID(id);

    public void LeaveLobby()
    {
        SteamGlobal.LeaveLobby();
        ClearAll();
    }

    // ── Scoring ────────────────────────────────────────────────────────────
    public void GetScores() => AccumulateScores(false);       // every inkable face
    public void GetTopDownScores() => AccumulateScores(true); // floors and slopes only

    void AccumulateScores(bool topDownOnly)
    {
        SurfaceInkManager[] managers = FindObjectsByType<SurfaceInkManager>(FindObjectsSortMode.None);
        if (managers.Length == 0) return;

        int pending = managers.Length;
        Vector3Int total = Vector3Int.zero;
        System.Action<Vector3Int> onSurface = scores =>
        {
            total += scores;
            pending--;
            if (pending == 0)
            {
                float t = total.x + total.y + total.z;
                if (t <= 0) return;
                string label = topDownOnly ? "[Top-down] " : "[Full] ";
                Debug.Log($"{label}Alpha: {total.x / t * 100:f2}%  Beta: {total.y / t * 100:f2}%  Neutral: {total.z / t * 100:f2}%");
            }
        };

        foreach (var mgr in managers)
        {
            if (topDownOnly) mgr.CheckTopScoresAsync(onSurface);
            else             mgr.CheckScoresAsync(onSurface);
        }
    }

    // ── Game start / ink reset ─────────────────────────────────────────────
    void OnGameStart()
    {
        if (!SteamGlobal.isHost) return;
        ClearAllInk();
        if (!devMode)
            SteamGlobal.SendAllData((ushort)NetMsg.InkReset,
                new MatchEventData { phase = MatchPhase.Playing, serverTime = Time.time });
    }

    void ClearAllInk()
    {
        if (surfaceManagers == null) return;
        foreach (var m in surfaceManagers) m.ClearInk();
    }

    // ── Lobby lifecycle ────────────────────────────────────────────────────
    void HandleLobbyUpdate(LobbyData data)
    {
        if (devMode) return;
        LobbyEvent ev = data.lobbyEvent;
        ulong who = data.dataUserID.Value;
        mainThread.Enqueue(() =>
        {
            switch (ev)
            {
                case LobbyEvent.LobbyCreated:
                case LobbyEvent.JoinedLobby:
                    SpawnLocalPlayer();
                    break;
                case LobbyEvent.PlayerLeft:
                    DespawnPlayer(who);
                    break;
                case LobbyEvent.LobbyClosed:
                    ClearAll();
                    break;
            }
        });
    }

    // ── Spawning ───────────────────────────────────────────────────────────
    void SpawnLocalPlayer()
    {
        if (localPlayer != null) return;
        localId = SteamGlobal.steamID.Value;

        int team = 1; // alternate by join order: even = alpha, odd = beta
        var members = SteamGlobal.GetLobbyPlayerInfo();
        if (members != null)
            for (int i = 0; i < members.Length; i++)
                if (members[i].Item2 == localId) { team = (i % 2 == 0) ? 1 : 2; break; }

        Vector3 pos = PickSpawnPoint(team);
        localPlayer = InstantiateEntity(pos, PlayerMode.Client, team, LocalPlayerName());
        LocalPlayer = localPlayer;
        players[localId] = localPlayer;

        SteamGlobal.SendAllData((ushort)NetMsg.PlayerSpawn, new PlayerSpawnData
        {
            steamId = localId, team = team, position = pos, isHostPlayer = SteamGlobal.isHost,
            playerName = LocalPlayerName()
        });
    }

    void SpawnRemotePlayer(PlayerSpawnData d)
    {
        if (d.steamId == localId) return;
        if (players.ContainsKey(d.steamId)) return;

        string name = !string.IsNullOrEmpty(d.playerName) ? d.playerName : RemotePlayerName(d.steamId);
        PlayerController pc = InstantiateEntity(d.position, PlayerMode.Network, d.team, name);
        players[d.steamId] = pc;

        if (localPlayer != null)
            SteamGlobal.SendDirectData((ushort)NetMsg.PlayerSpawn, new PlayerSpawnData
            {
                steamId      = localId,
                team         = localPlayer.Team,
                position     = localPlayer.transform.position,
                isHostPlayer = SteamGlobal.isHost,
                playerName   = LocalPlayerName()
            }, (SteamId)d.steamId);
    }

    PlayerController InstantiateEntity(Vector3 pos, PlayerMode mode, int team, string playerName)
    {
        GameObject go = Instantiate(playerEntityPrefab, pos, Quaternion.identity);
        // "VOID" in the prefab name is a placeholder for the player's name.
        go.name = playerEntityPrefab.name.Replace("VOID", playerName);
        PlayerController pc = go.transform.GetChild(0).GetComponent<PlayerController>();
        pc.SetPlayerMode(mode);
        pc.SetTeam(team);
        if (mode == PlayerMode.Network) DisableLocalOnlyComponents(go);
        return pc;
    }

    static void DisableLocalOnlyComponents(GameObject go)
    {
        foreach (var cam in go.GetComponentsInChildren<Camera>(true))          cam.enabled = false;
        foreach (var al  in go.GetComponentsInChildren<AudioListener>(true))   al.enabled  = false;
        foreach (var ws  in go.GetComponentsInChildren<WeaponShooter>(true))   ws.enabled  = false;
        foreach (var ca  in go.GetComponentsInChildren<CameraAim>(true))       ca.enabled  = false;
        foreach (var wca in go.GetComponentsInChildren<WeaponCameraAim>(true)) wca.enabled = false;
        foreach (var ui  in go.GetComponentsInChildren<UIController>(true))    ui.enabled  = false;
        // Off: a remote copy lags the real player and would splat the wrong place. Their ink arrives over the network.
        foreach (var ie  in go.GetComponentsInChildren<InkEmitter>(true))      ie.enabled  = false;
    }

    // Steam may be unavailable (dev mode), so fall back cleanly.
    static string LocalPlayerName()
    {
        try { if (SteamClient.IsValid) return SteamClient.Name; } catch { }
        return "LocalPlayer";
    }

    static string RemotePlayerName(ulong steamId)
    {
        var members = SteamGlobal.GetLobbyPlayerInfo();
        if (members != null)
            foreach (var m in members)
                if (m.Item2 == steamId && !string.IsNullOrEmpty(m.Item1)) return m.Item1;
        try { if (SteamClient.IsValid) return new Friend(steamId).Name; } catch { }
        return steamId.ToString();
    }

    static string HierarchyPath(Transform t)
    {
        string path = t.name;
        Transform cur = t.parent;
        while (cur != null) { path = cur.name + "/" + path; cur = cur.parent; }
        return path;
    }

    // The PlayerController sits on the entity root's first child; destroy the whole entity.
    static void DestroyEntity(PlayerController pc)
    {
        if (pc != null) Destroy(pc.transform.root.gameObject);
    }

    void DespawnPlayer(ulong steamId)
    {
        if (steamId == localId) return;
        if (players.TryGetValue(steamId, out var pc))
        {
            DestroyEntity(pc);
            players.Remove(steamId);
        }
    }

    // Leaving or losing the lobby removes every entity, local player included (rejoining respawns it).
    void ClearAll()
    {
        foreach (var kv in players) DestroyEntity(kv.Value);
        if (localPlayer != null && !players.ContainsValue(localPlayer)) DestroyEntity(localPlayer);
        players.Clear();
        localPlayer = null;
        LocalPlayer = null;
        localId = 0;
    }

    static Vector3 PickSpawnPoint(int team)
    {
        string tag = team == 1 ? "AlphaSpawn" : "BetaSpawn";
        GameObject[] pts = GameObject.FindGameObjectsWithTag(tag);
        if (pts.Length > 0) return pts[Random.Range(0, pts.Length)].transform.position;
        return new Vector3(0f, 3f, 0f);
    }

    // ── Main-thread drain ──────────────────────────────────────────────────
    void Update()
    {
        while (mainThread.TryDequeue(out var action)) action?.Invoke();
    }

    // ── Message handlers ───────────────────────────────────────────────────
    void OnPlayerSpawnMsg(object data, SteamId from)
    {
        if (data is PlayerSpawnData d) mainThread.Enqueue(() => SpawnRemotePlayer(d));
    }

    void OnPlayerDespawnMsg(object data, SteamId from)
    {
        if (data is PlayerSpawnData d) mainThread.Enqueue(() => DespawnPlayer(d.steamId));
    }

    void OnPlayerStateMsg(object data, SteamId from)
    {
        if (data is PlayerStateData s)
            mainThread.Enqueue(() =>
            {
                if (s.steamId == localId) return;
                if (players.TryGetValue(s.steamId, out var pc) && pc != null)
                    pc.ApplyNetState(s);
            });
    }

    void OnTeleportMsg(object data, SteamId from)
    {
        if (data is TeleportData d)
            mainThread.Enqueue(() =>
            {
                if (d.steamId == localId) return;
                if (players.TryGetValue(d.steamId, out var pc) && pc != null)
                    pc.ApplyTeleport(d.position, d.bodyYaw);
            });
    }

    void OnLocalSplat(SplatData data)
    {
        if (localPlayer == null) return;
        SteamGlobal.SendAllData((ushort)NetMsg.Splat, data);
    }

    void OnSplatMsg(object data, SteamId from)
    {
        if (data is SplatData d)
            mainThread.Enqueue(() =>
            {
                if (from.Value == localId) return;
                if (surfaceManagers == null || d.surfaceId < 0 || d.surfaceId >= surfaceManagers.Length) return;
                surfaceManagers[d.surfaceId].Splat(d.uv, d.splashSize, d.team, broadcast: false);
            });
    }

    void OnInkResetMsg(object data, SteamId from)
    {
        mainThread.Enqueue(ClearAllInk);
    }
}

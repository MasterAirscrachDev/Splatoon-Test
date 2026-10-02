using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
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
    [SerializeField] bool devMode; // local player only, no Steam

    [Header("Match (seconds)")]
    [SerializeField] float countdownTime = 3f;
    [SerializeField] float matchDuration = 180f;
    [SerializeField] float finalStretchTime = 60f; // "now or never": the coverage bar hides
    [SerializeField] float timesUpTime = 2.5f;
    [SerializeField] float resultsTime = 15f;

    // Team colours come in pairs; the host picks one at random when it loads and again for each
    // game, and everyone follows its pick (roster and match messages carry it).
    [System.Serializable]
    public struct TeamColours
    {
        public string name;
        public Color alpha, beta;
        public TeamColours(string name, Color alpha, Color beta) { this.name = name; this.alpha = alpha; this.beta = beta; }
    }

    [Header("Team Colours")]
    [SerializeField] TeamColours[] colourPairs =
    {
        new TeamColours("Cyan / Magenta",      new Color(0f, 1f, 1f),          new Color(1f, 0f, 1f)),
        new TeamColours("Orange / Blue",       new Color(1f, 0.5f, 0.05f),     new Color(0.15f, 0.35f, 1f)),
        new TeamColours("Lime / Purple",       new Color(0.7f, 1f, 0.1f),      new Color(0.55f, 0.15f, 0.95f)),
        new TeamColours("Yellow / Indigo",     new Color(1f, 0.85f, 0.05f),    new Color(0.3f, 0.2f, 0.9f)),
        new TeamColours("Pink / Green",        new Color(1f, 0.3f, 0.6f),      new Color(0.1f, 0.85f, 0.45f)),
        new TeamColours("Turquoise / Red",     new Color(0.1f, 0.9f, 0.75f),   new Color(1f, 0.25f, 0.15f)),
        new TeamColours("Sky / Gold",          new Color(0.35f, 0.75f, 1f),    new Color(1f, 0.65f, 0.1f)),
    };
    int colourPair = -1;

    public Color AlphaTeam => Colours.alpha;
    public Color BetaTeam  => Colours.beta;
    public int ColourPair => colourPair;
    public int ColourPairCount => colourPairs.Length;
    public string ColourPairName => Colours.name;
    TeamColours Colours => colourPairs.Length == 0 ? new TeamColours("Default", Color.cyan, Color.magenta)
                                                   : colourPairs[Mathf.Clamp(colourPair, 0, colourPairs.Length - 1)];

    // The team colours changed (a new pair): anything that applied them once should again.
    public static event System.Action TeamColoursChanged;

    public void SetColourPair(int index)
    {
        if (colourPairs.Length == 0) return;
        index = Mathf.Clamp(index, 0, colourPairs.Length - 1);
        if (index == colourPair) return;
        colourPair = index;
        TeamColoursChanged?.Invoke();
    }

    // A random pair, different from the current one when there's a choice.
    int RandomColourPair()
    {
        if (colourPairs.Length <= 1) return 0;
        int pick = UnityEngine.Random.Range(0, colourPairs.Length - 1);
        return colourPair >= 0 && pick >= colourPair ? pick + 1 : pick;
    }

    const string LobbyTag = "TestSplatoongame";

    readonly Dictionary<ulong, PlayerController> players = new Dictionary<ulong, PlayerController>();
    public Dictionary<ulong, PlayerController>.ValueCollection Players => players.Values;

    public MatchPhase Phase { get; private set; } = MatchPhase.FreeRoam;
    float phaseStartTime, phaseEndTime;
    public float PhaseElapsed => Time.time - phaseStartTime;
    public float PhaseTimeRemaining => Mathf.Max(0f, phaseEndTime - Time.time);
    // Full duration before the match starts, counting down while playing, 0 once it's over.
    public float MatchTimeRemaining =>
        Phase == MatchPhase.Playing ? PhaseTimeRemaining :
        Phase == MatchPhase.TimesUp || Phase == MatchPhase.Results ? 0f : matchDuration;
    public bool InMatch => Phase != MatchPhase.FreeRoam;
    public bool InFinalStretch => Phase == MatchPhase.Playing && PhaseTimeRemaining <= finalStretchTime;
    public Vector3Int MatchResult { get; private set; } // alpha, beta, neutral texels, from the host
    public static event System.Action<MatchPhase> PhaseChanged;
    public static event System.Action FinalStretchStarted; // "now or never" (music goes here)
    readonly ConcurrentQueue<System.Action> mainThread = new ConcurrentQueue<System.Action>();

    SurfaceInkManager[] surfaceManagers;
    PlayerController localPlayer;
    ulong localId;
    SteamNetwork steamNet;
    ControlLayer input;

    void Awake()
    {
        Instance = this;
        colourPair = RandomColourPair(); // this load's colours (joiners take the host's from its roster)

        var all = FindObjectsByType<SurfaceInkManager>(FindObjectsSortMode.None); // sorted by path so ids match on every client
        System.Array.Sort(all, (a, b) => HierarchyPath(a.transform).CompareTo(HierarchyPath(b.transform)));
        surfaceManagers = all;
        for (int i = 0; i < surfaceManagers.Length; i++)
            surfaceManagers[i].SurfaceId = i;

        if (devMode) Steam = SteamStatus.Unavailable;
        else
        {
            steamNet = FindFirstObjectByType<SteamNetwork>(); // its Start reports in, after every Awake
            if (steamNet != null) steamNet.onSteamSetup += OnSteamSetup;
            else Steam = SteamStatus.Unavailable;
        }
    }

    void Start()
    {
        input = new ControlLayer();
        input.Enable();
        input.GameControl.GameStart.performed += _ => OnGameStart();

        if (devMode)
        {
            localPlayer = InstantiateEntity(Vector3.zero, PlayerMode.Client, 1, LocalPlayerName(), localId);
            LocalPlayer = localPlayer;
            players[localId] = localPlayer;
        }
    }

    void OnDestroy()
    {
        if (Instance == this) { Instance = null; LocalPlayer = null; }
        if (steamNet != null) steamNet.onSteamSetup -= OnSteamSetup;
        input?.Disable();
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
        SteamGlobal.Bind((ushort)NetMsg.MatchEvent,    OnMatchEventMsg);
        SteamGlobal.Bind((ushort)NetMsg.Teleport,      OnTeleportMsg);
        SteamGlobal.Bind((ushort)NetMsg.ProjectileSpawn, OnProjectileSpawnMsg);
        SteamGlobal.Bind((ushort)NetMsg.Damage,        OnDamageMsg);
        SteamGlobal.Bind((ushort)NetMsg.TeamAssign,    OnTeamAssignMsg);
        SteamGlobal.Bind((ushort)NetMsg.SubSpawn,      OnSubSpawnMsg);
        SteamGlobal.Bind((ushort)NetMsg.SubDestroy,    OnSubDestroyMsg);
        SteamGlobal.Bind((ushort)NetMsg.SubDamage,     OnSubDamageMsg);
        SteamGlobal.Bind((ushort)NetMsg.InkStrike,     OnInkStrikeMsg);
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
        SteamGlobal.UnBind((ushort)NetMsg.MatchEvent,    OnMatchEventMsg);
        SteamGlobal.UnBind((ushort)NetMsg.Teleport,      OnTeleportMsg);
        SteamGlobal.UnBind((ushort)NetMsg.ProjectileSpawn, OnProjectileSpawnMsg);
        SteamGlobal.UnBind((ushort)NetMsg.Damage,        OnDamageMsg);
        SteamGlobal.UnBind((ushort)NetMsg.TeamAssign,    OnTeamAssignMsg);
        SteamGlobal.UnBind((ushort)NetMsg.SubSpawn,      OnSubSpawnMsg);
        SteamGlobal.UnBind((ushort)NetMsg.SubDestroy,    OnSubDestroyMsg);
        SteamGlobal.UnBind((ushort)NetMsg.SubDamage,     OnSubDamageMsg);
        SteamGlobal.UnBind((ushort)NetMsg.InkStrike,     OnInkStrikeMsg);
        SurfaceInkManager.OnSplatApplied -= OnLocalSplat;
    }

    // ── Lobbies (host / join, see LobbyMenu) ───────────────────────────────
    public enum SteamStatus { Connecting, Ready, Unavailable }
    public SteamStatus Steam { get; private set; } = SteamStatus.Connecting;
    public bool DevMode => devMode;
    public bool InLobby => localPlayer != null;

    public struct LobbyInfo
    {
        public SteamId id;
        public string host;
        public int players, maxPlayers;
    }

    void OnSteamSetup(bool success) => Steam = success ? SteamStatus.Ready : SteamStatus.Unavailable;

    // Public and tagged with LobbyTag, so FindLobbies lists it. Joining spawns everyone via OnLobbyUpdate.
    public async Task<bool> HostLobby()
    {
        if (Steam != SteamStatus.Ready || InLobby) return false;
        await SteamGlobal.CreateLobby(maxPlayers, HostMode.Public, new Dictionary<string, string> { { "game", LobbyTag } });
        return SteamGlobal.isHost;
    }

    public async Task<bool> JoinLobby(SteamId id)
    {
        if (Steam != SteamStatus.Ready || InLobby) return false;
        try { return await SteamGlobal.JoinLobbyFromID(id) != null; }
        catch (System.Exception e) { Debug.LogWarning($"[Lobby] Couldn't join {id}: {e.Message}"); return false; } // e.g. it closed meanwhile
    }

    // This game's public lobbies with room, worldwide.
    public async Task<List<LobbyInfo>> FindLobbies()
    {
        var result = new List<LobbyInfo>();
        if (Steam != SteamStatus.Ready) return result;
        var lobbies = await SteamGlobal.GetAllPublicLobbies(SteamLobbySearchDistance.Worldwide,
            new Dictionary<string, string> { { "game", LobbyTag } }, requireOpenSlots: 1);
        if (lobbies == null) return result;
        foreach (var lobby in lobbies)
        {
            string host = lobby.GetData("host");
            result.Add(new LobbyInfo
            {
                id = lobby.Id,
                host = string.IsNullOrEmpty(host) ? $"Lobby {lobby.Id.Value % 10000:0000}" : host,
                players = lobby.MemberCount,
                maxPlayers = lobby.MaxMembers
            });
        }
        return result;
    }

    public void LeaveLobby()
    {
        SteamGlobal.LeaveLobby();
        ClearAll();
    }

    // ── Scoring ────────────────────────────────────────────────────────────
    public void GetScores() => LogScores(false);       // every inkable face
    public void GetTopDownScores() => LogScores(true); // floors and slopes only

    void LogScores(bool topDownOnly)
    {
        RequestScores(topDownOnly, total =>
        {
            float t = total.x + total.y + total.z;
            if (t <= 0) return;
            string label = topDownOnly ? "[Top-down] " : "[Full] ";
            Debug.Log($"{label}Alpha: {total.x / t * 100:f2}%  Beta: {total.y / t * 100:f2}%  Neutral: {total.z / t * 100:f2}%");
        });
    }

    // Texel counts (alpha, beta, neutral) summed over every surface; the callback fires once
    // all surfaces' GPU readbacks complete.
    public void RequestScores(bool topDownOnly, System.Action<Vector3Int> callback)
    {
        if (surfaceManagers == null || surfaceManagers.Length == 0) return;

        int pending = surfaceManagers.Length;
        Vector3Int total = Vector3Int.zero;
        System.Action<Vector3Int> onSurface = scores =>
        {
            total += scores;
            if (--pending == 0) callback(total);
        };

        foreach (var mgr in surfaceManagers)
        {
            if (topDownOnly) mgr.CheckTopScoresAsync(onSurface);
            else             mgr.CheckScoresAsync(onSurface);
        }
    }

    // ── Host menu / match control ──────────────────────────────────────────
    public bool IsHost => devMode || SteamGlobal.isHost;
    public static event System.Action HostMenuToggled; // G, host only (see HostMenu)

    void OnGameStart()
    {
        if (IsHost) HostMenuToggled?.Invoke();
    }

    // Host, free roam only: clear the ink (and beacons) everywhere.
    public void ResetMap()
    {
        if (!IsHost || InMatch) return;
        ClearAllInk();
        Send(NetMsg.InkReset, new MatchEventData { phase = MatchPhase.FreeRoam, serverTime = Time.time });
    }

    // ── Match flow ─────────────────────────────────────────────────────────
    // The host runs the timeline and broadcasts each phase; every client (host included) enters
    // it the same way. Results carry the host's final turf, so everyone sees the same outcome.

    public void StartGame()
    {
        if (!IsHost || InMatch) return;
        SetColourPair(RandomColourPair()); // new game, new colours (sent with the phase)
        BroadcastPhase(MatchPhase.Countdown, countdownTime);
    }

    // Host: abandon the match and go back to free roam.
    public void EndMatch()
    {
        if (!IsHost || !InMatch) return;
        BroadcastPhase(MatchPhase.FreeRoam, 0f);
    }

    public void SetMatchTimings(float countdown, float match, float finalStretch, float timesUp, float results) // tests
    {
        countdownTime = countdown; matchDuration = match; finalStretchTime = finalStretch; timesUpTime = timesUp; resultsTime = results;
    }

    void BroadcastPhase(MatchPhase phase, float duration, Vector3Int result = default)
    {
        var data = new MatchEventData
        {
            phase = phase, serverTime = Time.time, duration = duration,
            alphaScore = result.x, betaScore = result.y, neutralScore = result.z,
            colourPair = colourPair
        };
        EnterPhase(data);
        Send(NetMsg.MatchEvent, data);
    }

    void EnterPhase(MatchEventData d)
    {
        SetColourPair(d.colourPair);
        Phase = d.phase;
        joinedLate = d.lateJoin || joinedLate && d.phase != MatchPhase.FreeRoam;
        phaseStartTime = Time.time;
        phaseEndTime = Time.time + d.duration;
        finalStretchAnnounced = false;
        InputGate.MatchLocked = d.phase == MatchPhase.Countdown || d.phase == MatchPhase.TimesUp || d.phase == MatchPhase.Results;

        if (d.phase == MatchPhase.Countdown)
        {
            ClearAllInk();
            PrepareLocalPlayer();
        }
        if (d.phase == MatchPhase.TimesUp && IsHost) RequestFinalScore();
        if (d.phase == MatchPhase.Results) MatchResult = new Vector3Int(d.alphaScore, d.betaScore, d.neutralScore);
        if (d.phase == MatchPhase.FreeRoam && IsHost) ReturnLateJoiners();
        PhaseChanged?.Invoke(d.phase);
    }

    // Host: until there's matchmaking, someone joining mid-match spectates (overhead view) and
    // sees the round from that point; they get their team back when it ends.
    readonly Dictionary<ulong, PlayerRole> lateJoiners = new Dictionary<ulong, PlayerRole>();
    bool joinedLate; // we're the late joiner (told by the host)
    public bool IsLateJoiner(ulong id) => localPlayer != null && id == localPlayer.OwnerId ? joinedLate : lateJoiners.ContainsKey(id);

    void BenchLateJoiner(ulong id)
    {
        PlayerController pc = GetPlayer(id);
        if (pc == null) return;
        lateJoiners[id] = pc.Role;
        pc.SetRole(PlayerRole.Spectator); // goes out with the roster broadcast that follows
        SendTo(id, NetMsg.MatchEvent, new MatchEventData
        {
            phase = Phase, serverTime = Time.time, duration = PhaseTimeRemaining,
            alphaScore = MatchResult.x, betaScore = MatchResult.y, neutralScore = MatchResult.z,
            lateJoin = true, colourPair = colourPair
        });
    }

    void ReturnLateJoiners()
    {
        var ids = new List<ulong>();
        var roles = new List<PlayerRole>();
        foreach (var kv in lateJoiners)
            if (GetPlayer(kv.Key) != null) { ids.Add(kv.Key); roles.Add(kv.Value); }
        lateJoiners.Clear();
        if (ids.Count > 0) SetRoster(ids.ToArray(), roles.ToArray());
    }

    // Everyone starts at their spawn, healthy, with a full tank and no special.
    void PrepareLocalPlayer()
    {
        PlayerController p = localPlayer;
        if (p == null || !p.gameObject.activeInHierarchy || p.Team == 0) return;
        p.CancelSuperJump();
        p.Respawn();
        if (p.Hitbox != null) p.Hitbox.ResetHealth();
        PlayerLoadout loadout = p.GetComponent<PlayerLoadout>();
        if (loadout != null) loadout.ResetForMatch();
        else p.RefillInk();
    }

    bool finalStretchAnnounced, finalScoreReady;
    Vector3Int finalScore;

    void RequestFinalScore()
    {
        finalScoreReady = false;
        RequestScores(true, total => { finalScore = total; finalScoreReady = true; });
    }

    // Host advances timed phases; everyone raises the final-stretch event locally.
    void UpdateMatch()
    {
        if (InFinalStretch && !finalStretchAnnounced)
        {
            finalStretchAnnounced = true;
            FinalStretchStarted?.Invoke();
        }
        if (!IsHost || !InMatch || Time.time < phaseEndTime) return;
        switch (Phase)
        {
            case MatchPhase.Countdown: BroadcastPhase(MatchPhase.Playing, matchDuration); break;
            case MatchPhase.Playing:   BroadcastPhase(MatchPhase.TimesUp, timesUpTime); break;
            case MatchPhase.TimesUp:
                if (!finalScoreReady && Time.time < phaseEndTime + 3f) break; // readback still coming
                BroadcastPhase(MatchPhase.Results, resultsTime, finalScore);
                break;
            case MatchPhase.Results:   BroadcastPhase(MatchPhase.FreeRoam, 0f); break;
        }
    }

    void OnMatchEventMsg(object data, SteamId from)
    {
        if (data is MatchEventData d)
            mainThread.Enqueue(() =>
            {
                if (!devMode && from != SteamGlobal.hostID) return; // only the host runs the match
                if (IsHost) return;                               // we already entered it
                EnterPhase(d);
            });
    }

    // Resetting the map also removes every sub.
    void ClearAllInk()
    {
        SubDevice.RemoveAll();
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
        localPlayer = InstantiateEntity(pos, PlayerMode.Client, team, LocalPlayerName(), localId);
        LocalPlayer = localPlayer;
        players[localId] = localPlayer;

        Send(NetMsg.PlayerSpawn, new PlayerSpawnData
        {
            steamId = localId, team = team, position = pos, isHostPlayer = SteamGlobal.isHost,
            playerName = LocalPlayerName()
        });
    }

    void SpawnRemotePlayer(PlayerSpawnData d)
    {
        if (d.steamId == localId) return;
        if (players.ContainsKey(d.steamId)) return;
        SpawnRemoteEntity(d);
        if (IsHost && InMatch) BenchLateJoiner(d.steamId);
        if (IsHost) BroadcastRoster(); // so the newcomer learns everyone's role
    }

    void SpawnRemoteEntity(PlayerSpawnData d)
    {
        string name = !string.IsNullOrEmpty(d.playerName) ? d.playerName : RemotePlayerName(d.steamId);
        PlayerController pc = InstantiateEntity(d.position, PlayerMode.Network, d.team, name, d.steamId);
        players[d.steamId] = pc;

        if (localPlayer != null)
            SendTo(d.steamId, NetMsg.PlayerSpawn, new PlayerSpawnData
            {
                steamId      = localId,
                team         = localPlayer.Team,
                position     = localPlayer.transform.position,
                isHostPlayer = SteamGlobal.isHost,
                playerName   = LocalPlayerName()
            });
    }

    PlayerController InstantiateEntity(Vector3 pos, PlayerMode mode, int team, string playerName, ulong steamId)
    {
        GameObject go = Instantiate(playerEntityPrefab, pos, Quaternion.identity);
        // "VOID" in the prefab name is a placeholder for the player's name.
        go.name = playerEntityPrefab.name.Replace("VOID", playerName);
        PlayerController pc = go.transform.GetChild(0).GetComponent<PlayerController>();
        pc.SetPlayerMode(mode);
        pc.SetTeam(team);
        pc.SetOwner(steamId);
        pc.SetDisplayName(playerName);
        if (mode == PlayerMode.Network) DisableLocalOnlyComponents(go);
        return pc;
    }

    // Tests, dev mode only: another locally simulated player, not part of the roster or network
    // (see PlayerController.IsTestPlayer). Its root can be deactivated to park it.
    public PlayerController SpawnTestPlayer(Vector3 pos, int team, ulong id)
    {
        if (!devMode) return null;
        PlayerController pc = InstantiateEntity(pos, PlayerMode.Client, team, $"Tester{id % 100}", id);
        pc.IsTestPlayer = true;
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
        lateJoiners.Remove(steamId);
        SubDevice.RemoveOwnedBy(steamId);
    }

    // Leaving or losing the lobby removes every entity, local player included (rejoining respawns it).
    void ClearAll()
    {
        foreach (var kv in players) DestroyEntity(kv.Value);
        SubDevice.RemoveAll();
        if (InMatch) EnterPhase(new MatchEventData { phase = MatchPhase.FreeRoam }); // unlock, drop the results view
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
        UpdateMatch();
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
        Send(NetMsg.Splat, data);
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
        mainThread.Enqueue(() =>
        {
            if (!devMode && from != SteamGlobal.hostID) return;
            ClearAllInk();
        });
    }

    // ── Roster ─────────────────────────────────────────────────────────────
    // Host: assign every listed player a role and broadcast the result.
    public void SetRoster(ulong[] ids, PlayerRole[] roles)
    {
        if (!IsHost) return;
        int[] r = new int[roles.Length];
        for (int i = 0; i < roles.Length; i++) r[i] = (int)roles[i];
        ApplyRoster(ids, r);
        Send(NetMsg.TeamAssign, new TeamAssignData { ids = ids, roles = r, colourPair = colourPair });
    }

    void BroadcastRoster()
    {
        var ids = new List<ulong>();
        var roles = new List<PlayerRole>();
        foreach (var kv in players)
            if (kv.Value != null) { ids.Add(kv.Key); roles.Add(kv.Value.Role); }
        SetRoster(ids.ToArray(), roles.ToArray());
    }

    void ApplyRoster(ulong[] ids, int[] roles)
    {
        for (int i = 0; i < ids.Length && i < roles.Length; i++)
        {
            PlayerController pc = GetPlayer(ids[i]);
            if (pc != null) pc.SetRole((PlayerRole)roles[i]);
        }
    }

    void OnTeamAssignMsg(object data, SteamId from)
    {
        if (data is TeamAssignData d && d.ids != null && d.roles != null)
            mainThread.Enqueue(() =>
            {
                if (!devMode && from != SteamGlobal.hostID) return; // only the host assigns teams
                SetColourPair(d.colourPair);
                ApplyRoster(d.ids, d.roles);
            });
    }

    // ── Combat ─────────────────────────────────────────────────────────────
    // The shooter is authoritative: its projectiles paint (Splat messages) and decide hits.
    // Other clients replay each volley as visual-only projectiles, and a hit on a remote copy
    // is forwarded to that player's client, which owns its health.

    public PlayerController GetPlayer(ulong steamId) =>
        players.TryGetValue(steamId, out var pc) ? pc : null;

    // One message per volley; unreliable since the replay is purely visual.
    public void BroadcastShots(PlayerController shooter, Vector3 origin, Vector3 inherit, float[] velocities, int[] splashSizes, bool[] visible)
    {
        Send(NetMsg.ProjectileSpawn, new ProjectileSpawnData
        {
            shooterSteamId = shooter.OwnerId,
            team           = shooter.Team,
            origin         = origin,
            inherit        = inherit,
            velocities     = velocities,
            splashSizes    = splashSizes,
            visible        = visible
        }, reliable: false);
    }

    public void SendDamage(ulong target, float amount, int fromTeam, ulong attacker, string source = null)
    {
        SendTo(target, NetMsg.Damage, new DamageData
        {
            targetSteamId = target, attackerSteamId = attacker, amount = amount, fromTeam = fromTeam, source = source
        });
    }

    void OnProjectileSpawnMsg(object data, SteamId from)
    {
        if (data is ProjectileSpawnData d) mainThread.Enqueue(() => SpawnRemoteShots(d));
    }

    // Visual-only copies of their volley, flown with their equipped weapon's ballistics.
    void SpawnRemoteShots(ProjectileSpawnData d)
    {
        if (d.shooterSteamId == localId || d.splashSizes == null) return;
        PlayerController shooter = GetPlayer(d.shooterSteamId);
        if (shooter == null) return;
        PlayerLoadout loadout = shooter.GetComponent<PlayerLoadout>();
        WeaponShooter weapon = loadout != null && loadout.CurrentWeapon is WeaponShooter held ? held : shooter.GetComponentInChildren<WeaponShooter>(true);
        if (weapon == null || weapon.ProjectilePrefab == null) return;

        Vector3 origin = d.origin;
        Vector3 inherit = d.inherit != null ? (Vector3)d.inherit : Vector3.zero;
        int count = Mathf.Min(d.splashSizes.Length, d.visible.Length, d.velocities.Length / 3);
        for (int i = 0; i < count; i++)
        {
            Vector3 v = new Vector3(d.velocities[i * 3], d.velocities[i * 3 + 1], d.velocities[i * 3 + 2]);
            ProjectileManager.Fire(weapon.ProjectilePrefab, origin, v, d.splashSizes[i], d.team, d.visible[i],
                                   authoritative: false, ownerId: d.shooterSteamId, ballistics: weapon.Ballistics, inherit: inherit);
        }
    }

    void OnDamageMsg(object data, SteamId from)
    {
        if (data is DamageData d)
            mainThread.Enqueue(() =>
            {
                if (d.targetSteamId != localId || localPlayer == null || localPlayer.Hitbox == null) return;
                localPlayer.Hitbox.TakeDamage(d.amount, d.fromTeam, d.attackerSteamId, d.source);
            });
    }

    // ── Subs (beacon, sprinkler) ───────────────────────────────────────────
    // The owner is authoritative: it spawns, damages and expires its subs. Anyone may break one
    // (landing on a beacon), so SubDestroy is accepted from every client.

    // On placing/throwing, and again when a thrown sub lands.
    public void SendSubSpawn(SubDevice sub)
    {
        var d = new SubData
        {
            ownerId = sub.OwnerId, subId = sub.Id, team = sub.Team, subType = (int)sub.Type,
            position = sub.transform.position, velocity = Vector3.zero, normal = sub.transform.up, landed = true
        };
        if (sub is Sprinkler s) { d.landed = s.Landed; d.velocity = s.Velocity; }
        Send(NetMsg.SubSpawn, d);
    }

    public void SendSubDestroy(ulong ownerId, int subId) =>
        Send(NetMsg.SubDestroy, new SubData { ownerId = ownerId, subId = subId });

    public void SendSubDamage(ulong ownerId, int subId, float amount, int fromTeam, ulong attacker) =>
        SendTo(ownerId, NetMsg.SubDamage, new SubDamageData
        {
            ownerId = ownerId, subId = subId, amount = amount, fromTeam = fromTeam, attackerSteamId = attacker
        });

    void OnSubSpawnMsg(object data, SteamId from)
    {
        if (data is SubData d)
            mainThread.Enqueue(() =>
            {
                if (d.ownerId == localId && localPlayer != null) return; // our own
                PlayerController owner = GetPlayer(d.ownerId);
                PlayerLoadout loadout = owner != null ? owner.GetComponent<PlayerLoadout>() : null;
                if (loadout != null) loadout.SpawnRemoteSub(d);
            });
    }

    void OnSubDestroyMsg(object data, SteamId from)
    {
        if (data is SubData d)
            mainThread.Enqueue(() =>
            {
                SubDevice sub = SubDevice.Find(d.ownerId, d.subId);
                if (sub != null) sub.Remove();
            });
    }

    void OnSubDamageMsg(object data, SteamId from)
    {
        if (data is SubDamageData d)
            mainThread.Enqueue(() =>
            {
                SubDevice sub = SubDevice.Find(d.ownerId, d.subId);
                if (sub != null && sub.IsOwnedLocally) sub.TakeDamage(d.amount, d.fromTeam, d.attackerSteamId);
            });
    }

    // ── Specials ───────────────────────────────────────────────────────────
    // The caller's client paints and deals the damage; everyone else just shows the strike.

    public void SendInkStrike(ulong ownerId, int team, Vector3 target) =>
        Send(NetMsg.InkStrike, new InkStrikeData { ownerId = ownerId, team = team, position = target });

    void OnInkStrikeMsg(object data, SteamId from)
    {
        if (data is InkStrikeData d)
            mainThread.Enqueue(() =>
            {
                if (d.ownerId == localId && localPlayer != null) return; // our own
                PlayerController owner = GetPlayer(d.ownerId);
                PlayerLoadout loadout = owner != null ? owner.GetComponent<PlayerLoadout>() : null;
                if (loadout != null) loadout.SpawnRemoteInkStrike(d);
            });
    }

    // ── Sending (and test hooks) ───────────────────────────────────────────
    // Tests: when set, outgoing messages go here instead of Steam (target 0 = everyone).
    public static System.Action<NetMsg, object, ulong> SendOverride;

    void Send(NetMsg id, object data, bool reliable = true)
    {
        if (SendOverride != null) { SendOverride(id, data, 0); return; }
        SteamGlobal.SendAllData((ushort)id, data, reliable);
    }

    void SendTo(ulong target, NetMsg id, object data, bool reliable = true)
    {
        if (SendOverride != null) { SendOverride(id, data, target); return; }
        SteamGlobal.SendDirectData((ushort)id, data, (SteamId)target, reliable);
    }

    // Tests: deliver a message as if it had arrived from `from`.
    public void Receive(NetMsg id, object data, ulong from)
    {
        SteamId sender = (SteamId)from;
        switch (id)
        {
            case NetMsg.PlayerSpawn:     OnPlayerSpawnMsg(data, sender); break;
            case NetMsg.PlayerDespawn:   OnPlayerDespawnMsg(data, sender); break;
            case NetMsg.PlayerState:     OnPlayerStateMsg(data, sender); break;
            case NetMsg.Splat:           OnSplatMsg(data, sender); break;
            case NetMsg.InkReset:        OnInkResetMsg(data, sender); break;
            case NetMsg.MatchEvent:      OnMatchEventMsg(data, sender); break;
            case NetMsg.Teleport:        OnTeleportMsg(data, sender); break;
            case NetMsg.ProjectileSpawn: OnProjectileSpawnMsg(data, sender); break;
            case NetMsg.Damage:          OnDamageMsg(data, sender); break;
            case NetMsg.TeamAssign:      OnTeamAssignMsg(data, sender); break;
            case NetMsg.SubSpawn:        OnSubSpawnMsg(data, sender); break;
            case NetMsg.SubDestroy:      OnSubDestroyMsg(data, sender); break;
            case NetMsg.SubDamage:       OnSubDamageMsg(data, sender); break;
            case NetMsg.InkStrike:       OnInkStrikeMsg(data, sender); break;
        }
    }
}

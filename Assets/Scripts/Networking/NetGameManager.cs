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

    [Header("Match")]
    [SerializeField] float matchDuration = 180f; // seconds

    [Header("Team Colours")]
    [SerializeField] Color alphaTeam = Color.cyan;
    [SerializeField] Color betaTeam  = Color.magenta;
    public Color AlphaTeam => alphaTeam;
    public Color BetaTeam  => betaTeam;

    const string LobbyTag = "TestSplatoongame";

    readonly Dictionary<ulong, PlayerController> players = new Dictionary<ulong, PlayerController>();
    public Dictionary<ulong, PlayerController>.ValueCollection Players => players.Values;

    public MatchPhase Phase { get; private set; } = MatchPhase.Lobby;
    float matchEndTime;
    // Full duration before the match starts, counting down while playing, 0 once ended.
    public float MatchTimeRemaining =>
        Phase == MatchPhase.Playing ? Mathf.Max(0f, matchEndTime - Time.time) :
        Phase == MatchPhase.Ended ? 0f : matchDuration;
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
            localPlayer = InstantiateEntity(Vector3.zero, PlayerMode.Client, 1, LocalPlayerName(), localId);
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
        SteamGlobal.Bind((ushort)NetMsg.ProjectileSpawn, OnProjectileSpawnMsg);
        SteamGlobal.Bind((ushort)NetMsg.Damage,        OnDamageMsg);
        SteamGlobal.Bind((ushort)NetMsg.TeamAssign,    OnTeamAssignMsg);
        SteamGlobal.Bind((ushort)NetMsg.BeaconSpawn,   OnBeaconSpawnMsg);
        SteamGlobal.Bind((ushort)NetMsg.BeaconDestroy, OnBeaconDestroyMsg);
        SteamGlobal.Bind((ushort)NetMsg.BeaconDamage,  OnBeaconDamageMsg);
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
        SteamGlobal.UnBind((ushort)NetMsg.ProjectileSpawn, OnProjectileSpawnMsg);
        SteamGlobal.UnBind((ushort)NetMsg.Damage,        OnDamageMsg);
        SteamGlobal.UnBind((ushort)NetMsg.TeamAssign,    OnTeamAssignMsg);
        SteamGlobal.UnBind((ushort)NetMsg.BeaconSpawn,   OnBeaconSpawnMsg);
        SteamGlobal.UnBind((ushort)NetMsg.BeaconDestroy, OnBeaconDestroyMsg);
        SteamGlobal.UnBind((ushort)NetMsg.BeaconDamage,  OnBeaconDamageMsg);
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

    // Placeholder until there's a proper pre-match flow.
    public void StartGame()
    {
        if (!IsHost) return;
        Debug.Log("[Match] Start game isn't implemented yet");
    }

    // Host: clear the ink and restart the timer everywhere.
    public void ResetMap()
    {
        if (!IsHost) return;
        ClearAllInk();
        BeginMatch(matchDuration);
        if (!devMode)
            Send(NetMsg.InkReset, new MatchEventData { phase = MatchPhase.Playing, serverTime = Time.time, duration = matchDuration });
    }

    void BeginMatch(float duration)
    {
        Phase = MatchPhase.Playing;
        matchEndTime = Time.time + duration;
    }

    // Resetting the map also removes every beacon.
    void ClearAllInk()
    {
        Beacon.RemoveAll();
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
        Beacon.RemoveOwnedBy(steamId);
    }

    // Leaving or losing the lobby removes every entity, local player included (rejoining respawns it).
    void ClearAll()
    {
        foreach (var kv in players) DestroyEntity(kv.Value);
        Beacon.RemoveAll();
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
        if (Phase == MatchPhase.Playing && Time.time >= matchEndTime)
        {
            Phase = MatchPhase.Ended;
            GetTopDownScores(); // final result to the log until there's a results screen
        }

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
        float duration = data is MatchEventData m && m.phase == MatchPhase.Playing && m.duration > 0f ? m.duration : matchDuration;
        mainThread.Enqueue(() =>
        {
            ClearAllInk();
            BeginMatch(duration);
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
        Send(NetMsg.TeamAssign, new TeamAssignData { ids = ids, roles = r });
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
    public void BroadcastShots(PlayerController shooter, Vector3 origin, float[] velocities, int[] splashSizes, bool[] visible)
    {
        Send(NetMsg.ProjectileSpawn, new ProjectileSpawnData
        {
            shooterSteamId = shooter.OwnerId,
            team           = shooter.Team,
            origin         = origin,
            velocities     = velocities,
            splashSizes    = splashSizes,
            visible        = visible
        }, reliable: false);
    }

    public void SendDamage(ulong target, float amount, int fromTeam, ulong attacker)
    {
        SendTo(target, NetMsg.Damage, new DamageData
        {
            targetSteamId = target, attackerSteamId = attacker, amount = amount, fromTeam = fromTeam
        });
    }

    void OnProjectileSpawnMsg(object data, SteamId from)
    {
        if (data is ProjectileSpawnData d) mainThread.Enqueue(() => SpawnRemoteShots(d));
    }

    void SpawnRemoteShots(ProjectileSpawnData d)
    {
        if (d.shooterSteamId == localId || d.splashSizes == null) return;
        PlayerController shooter = GetPlayer(d.shooterSteamId);
        if (shooter == null) return;
        WeaponShooter weapon = shooter.GetComponentInChildren<WeaponShooter>(true);
        if (weapon == null || weapon.ProjectilePrefab == null) return;

        Vector3 origin = d.origin;
        int count = Mathf.Min(d.splashSizes.Length, d.visible.Length, d.velocities.Length / 3);
        for (int i = 0; i < count; i++)
        {
            Vector3 v = new Vector3(d.velocities[i * 3], d.velocities[i * 3 + 1], d.velocities[i * 3 + 2]);
            Quaternion rot = v.sqrMagnitude > 0.0001f ? Quaternion.LookRotation(v) : Quaternion.identity;
            ProjectilePool.Get(weapon.ProjectilePrefab, origin, rot)
                          .Setup(v, d.splashSizes[i], d.team, d.visible[i], authoritative: false, ownerId: d.shooterSteamId);
        }
    }

    void OnDamageMsg(object data, SteamId from)
    {
        if (data is DamageData d)
            mainThread.Enqueue(() =>
            {
                if (d.targetSteamId != localId || localPlayer == null || localPlayer.Hitbox == null) return;
                localPlayer.Hitbox.TakeDamage(d.amount, d.fromTeam, d.attackerSteamId);
            });
    }

    // ── Beacons (sub) ──────────────────────────────────────────────────────
    // The owner is authoritative: it spawns, damages and expires its beacons. Anyone may break
    // one by landing on it, so BeaconDestroy is accepted from every client.

    public void SendBeaconSpawn(Beacon b) => Send(NetMsg.BeaconSpawn, new BeaconData
    {
        ownerId = b.OwnerId, beaconId = b.Id, team = b.Team, position = b.transform.position
    });

    public void SendBeaconDestroy(ulong ownerId, int beaconId) =>
        Send(NetMsg.BeaconDestroy, new BeaconData { ownerId = ownerId, beaconId = beaconId });

    public void SendBeaconDamage(ulong ownerId, int beaconId, float amount, int fromTeam, ulong attacker) =>
        SendTo(ownerId, NetMsg.BeaconDamage, new BeaconDamageData
        {
            ownerId = ownerId, beaconId = beaconId, amount = amount, fromTeam = fromTeam, attackerSteamId = attacker
        });

    void OnBeaconSpawnMsg(object data, SteamId from)
    {
        if (data is BeaconData d)
            mainThread.Enqueue(() =>
            {
                if (d.ownerId == localId && localPlayer != null) return; // our own
                PlayerController owner = GetPlayer(d.ownerId);
                PlayerLoadout loadout = owner != null ? owner.GetComponent<PlayerLoadout>() : null;
                if (loadout != null) loadout.SpawnRemoteBeacon(d);
            });
    }

    void OnBeaconDestroyMsg(object data, SteamId from)
    {
        if (data is BeaconData d)
            mainThread.Enqueue(() =>
            {
                Beacon b = Beacon.Find(d.ownerId, d.beaconId);
                if (b != null) b.Remove();
            });
    }

    void OnBeaconDamageMsg(object data, SteamId from)
    {
        if (data is BeaconDamageData d)
            mainThread.Enqueue(() =>
            {
                Beacon b = Beacon.Find(d.ownerId, d.beaconId);
                if (b != null && b.IsOwnedLocally) b.TakeDamage(d.amount, d.fromTeam, d.attackerSteamId);
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
            case NetMsg.Teleport:        OnTeleportMsg(data, sender); break;
            case NetMsg.ProjectileSpawn: OnProjectileSpawnMsg(data, sender); break;
            case NetMsg.Damage:          OnDamageMsg(data, sender); break;
            case NetMsg.TeamAssign:      OnTeamAssignMsg(data, sender); break;
            case NetMsg.BeaconSpawn:     OnBeaconSpawnMsg(data, sender); break;
            case NetMsg.BeaconDestroy:   OnBeaconDestroyMsg(data, sender); break;
            case NetMsg.BeaconDamage:    OnBeaconDamageMsg(data, sender); break;
        }
    }
}

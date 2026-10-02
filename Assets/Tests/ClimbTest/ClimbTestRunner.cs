using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;
using UnityEngine.InputSystem;
using static ClimbTestLayout;

// Runs scripted scenarios in ClimbTest.unity and logs "[ClimbTest] PASS/FAIL" plus a summary (also
// shown on screen). Fixed 1/75 s steps make runs repeatable. Station scenarios run side by side on
// several players (one per station at a time, see RunParallel); the Lobby ones, which share match,
// HUD and network state, run one at a time on the real local player. Watched from overhead.
// Manual use: F1–F9 teleport to stations 1–9, F11 toggles overhead/player view, F12 reruns everything.
public class ClimbTestRunner : MonoBehaviour
{
    [SerializeField] bool runOnStart = true;
    [Tooltip("Run only scenarios whose name contains this text (empty = all).")]
    [SerializeField] string filter = "";
    [Tooltip("Leave play mode once an automatic run finishes (for command-line runs).")]
    [SerializeField] bool exitPlayModeWhenDone = false;
    [Tooltip("Projectile prefab for the pooling scenario (assigned by ClimbTestSceneBuilder).")]
    [SerializeField] GameObject projectilePrefab;
    [Tooltip("Write a per-frame CSV of each scenario to <project>/Logs/ClimbTest/.")]
    [SerializeField] bool writeTraces = true;

    const float Dt = 1f / 75f;
    const int Lanes = 4; // players running station scenarios at once (the local player and test players)
    static readonly Vector2 Fwd = Vector2.up, Back = Vector2.down, Still = Vector2.zero;

    struct Frame
    {
        public float t;             // seconds since the scenario started
        public Vector3 pos;         // body centre, station-local
        public bool climbing;       // PlayerController.IsClimbing
        public bool contact;        // PlayerController.HasWallContact
        public bool inInk;          // PlayerController.IsInInk (drives squid hide/show)
        public bool squid, grounded, controllerOn, dead;
        public int team;
        public Vector3 normal;      // climb normal (world)
        public Quaternion squidRot; // squid visual rotation
        public int surfaceTeam;     // ink team directly below
        public float velY, speed;   // PlayerController's vertical velocity and current move speed
        public CollisionFlags flags;
        public bool flying;         // Super Jump flight: moves fast along a scripted arc
        public string phase;
    }

    struct Scenario
    {
        public string name;
        public Station station;
        public string spawn;
        public Func<IEnumerator> body;
    }

    struct Result { public string name; public bool pass; public string detail; }

    // One player working through scenarios. The members below resolve to the lane being stepped,
    // so scenarios read as single-player code whichever lane runs them.
    class Lane
    {
        public PlayerController player;
        public Transform station;
        public readonly List<Frame> frames = new List<Frame>();
        public readonly List<string> failures = new List<string>();
        public readonly List<string> notes = new List<string>();
        public int errorLogs;
    }

    readonly List<Lane> lanes = new List<Lane>();
    readonly HashSet<Lane> activeLanes = new HashSet<Lane>();
    Lane mainLane = new Lane(), lane;
    PlayerController player { get => lane.player; set => lane.player = value; }
    Transform station { get => lane.station; set => lane.station = value; }
    List<Frame> frames => lane.frames;
    List<string> failures => lane.failures;
    List<string> notes => lane.notes;
    int errorLogs { get => lane.errorLogs; set => lane.errorLogs = value; }
    readonly List<Result> results = new List<Result>();
    Camera overhead;
    // Errors logged during scene startup, reported as their own failing result.
    int startupErrors;
    string firstStartupError;
    bool running;
    Vector2 scroll;

    void Awake()
    {
        lane = mainLane;
        Application.logMessageReceived += OnStartupLog;
    }
    void OnDestroy() => Application.logMessageReceived -= OnStartupLog;

    void OnStartupLog(string message, string stack, LogType type)
    {
        if (type != LogType.Error && type != LogType.Exception && type != LogType.Assert) return;
        if (startupErrors++ == 0) firstStartupError = message;
    }

    // ── Lifecycle ───────────────────────────────────────────────────────────

    IEnumerator Start()
    {
        lane = mainLane;
        lanes.Add(mainLane);
        while ((player = NetGameManager.LocalPlayer) == null) yield return null;

        // Extra players for the station scenarios, parked (inactive) between runs.
        Transform lobby = GameObject.Find(Name(Station.Lobby)).transform;
        for (int i = 1; i < Lanes; i++)
        {
            PlayerController extra = NetGameManager.Instance.SpawnTestPlayer(lobby.TransformPoint(new Vector3(-10f + 2f * i, 1f, 10f)), player.Team, 9000UL + (ulong)i);
            if (extra != null) lanes.Add(new Lane { player = extra });
        }
        for (int i = 0; i < 10; i++) yield return null; // let TestInkFill and its readbacks land (and the extras start)
        foreach (Lane l in lanes) if (l != mainLane) l.player.transform.root.gameObject.SetActive(false);
        SetOverhead(true);
        if (runOnStart) yield return RunAll();
    }

    // The whole test level from above (instead of the local player's camera).
    void SetOverhead(bool on)
    {
        if (overhead == null)
        {
            overhead = new GameObject("TestOverheadCamera").AddComponent<Camera>();
            overhead.tag = "MainCamera";
            overhead.depth = 10;
            overhead.cullingMask = ~LayerMask.GetMask("UI");
            LevelView.Frame(overhead, LayerMask.GetMask("InkSurface"), 60f, 0f, (float)Screen.width / Screen.height, 1.05f);
        }
        overhead.enabled = on;
        foreach (Camera cam in mainLane.player.CameraRig.GetComponentsInChildren<Camera>(true)) cam.enabled = !on;
    }

    void Update()
    {
        Keyboard kb = Keyboard.current;
        if (kb == null || running || player == null) return;
        if (kb.f12Key.wasPressedThisFrame) { StartCoroutine(RunAll()); return; }
        if (kb.f11Key.wasPressedThisFrame) { SetOverhead(!overhead.enabled); return; }
        for (int i = 1; i <= 9; i++)
            if (kb[(Key)((int)Key.F1 + i - 1)].wasPressedThisFrame)
                Teleport((Station)i, "Spawn");
    }

    void Teleport(Station s, string spawnName)
    {
        station = GameObject.Find(Name(s)).transform;
        Transform spawn = station.Find(spawnName);
        player.PlaceAt(spawn.position, spawn.eulerAngles.y);
    }

    IEnumerator RunAll()
    {
        running = true;
        results.Clear();
        Application.logMessageReceived -= OnStartupLog;
        if (startupErrors > 0)
        {
            string detail = $"{startupErrors} error(s) during scene startup, first: {firstStartupError}";
            results.Add(new Result { name = "Scene startup is error-free", pass = false, detail = detail });
            Debug.LogWarning($"[ClimbTest] FAIL Scene startup is error-free :: {detail}");
        }

        Application.logMessageReceived += OnLog;
        Time.captureDeltaTime = Dt;

        var chosen = Scenarios().Where(s => string.IsNullOrEmpty(filter) || s.name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
        yield return RunParallel(chosen.Where(s => s.station != Station.Lobby).ToList());
        lane = mainLane;
        foreach (Scenario s in chosen.Where(s => s.station == Station.Lobby))
            yield return RunScenario(s);

        Time.captureDeltaTime = 0f;
        player.ScriptedInput = null;
        NetGameManager.SendOverride = null;
        Application.logMessageReceived -= OnLog;

        int passed = results.Count(r => r.pass);
        StringBuilder sb = new StringBuilder($"[ClimbTest] SUMMARY {passed}/{results.Count} passed\n");
        foreach (Result r in results) sb.AppendLine($"  {(r.pass ? "PASS" : "FAIL")}  {r.name}");
        Debug.Log(sb.ToString());
        running = false;

#if UNITY_EDITOR
        if (exitPlayModeWhenDone) UnityEditor.EditorApplication.isPlaying = false;
#endif
    }

    // Station scenarios across all lanes: each idle lane takes the next scenario whose station is
    // free, and every lane advances one step per frame (with `lane` set to it while it does).
    IEnumerator RunParallel(List<Scenario> queue)
    {
        foreach (Lane l in lanes) l.player.transform.root.gameObject.SetActive(true);
        var busy = new Dictionary<Lane, (Scenario s, Stack<IEnumerator> steps)>();
        while (queue.Count > 0 || busy.Count > 0)
        {
            foreach (Lane l in lanes)
            {
                if (busy.ContainsKey(l)) continue;
                int next = queue.FindIndex(q => busy.Values.All(b => b.s.station != q.station));
                if (next < 0) break;
                Scenario s = queue[next];
                queue.RemoveAt(next);
                var steps = new Stack<IEnumerator>();
                steps.Push(RunScenario(s));
                busy[l] = (s, steps);
            }
            foreach (Lane l in busy.Keys.ToList())
            {
                lane = l;
                if (!Step(busy[l].steps)) busy.Remove(l);
            }
            lane = mainLane;
            yield return null;
        }
        foreach (Lane l in lanes) if (l != mainLane) l.player.transform.root.gameObject.SetActive(false); // park
    }

    // Runs a (nested) coroutine up to its next frame wait. False once it has finished.
    static bool Step(Stack<IEnumerator> steps)
    {
        while (steps.Count > 0)
        {
            IEnumerator top = steps.Peek();
            if (!top.MoveNext()) { steps.Pop(); continue; }
            if (top.Current is IEnumerator nested) { steps.Push(nested); continue; }
            return true;
        }
        return false;
    }

    IEnumerator RunScenario(Scenario s)
    {
        frames.Clear(); failures.Clear(); notes.Clear(); errorLogs = 0;
        activeLanes.Add(lane);
        player.ScriptedInput = new ScriptedPlayerInput();
        Teleport(s.station, s.spawn ?? "Spawn");
        for (int i = 0; i < 15; i++) yield return null; // settle on the floor, standing
        frames.Clear();

        yield return s.body();

        CheckInvariants();
        if (errorLogs > 0) Fail($"{errorLogs} error/exception log(s) during the scenario");
        activeLanes.Remove(lane);
        WriteTrace(results.Count, s.name);

        bool pass = failures.Count == 0;
        string detail = string.Join("; ", failures.Concat(notes.Select(n => "(" + n + ")")));
        results.Add(new Result { name = s.name, pass = pass, detail = detail });
        string line = $"[ClimbTest] {(pass ? "PASS" : "FAIL")} {s.name} :: {detail}";
        if (pass) Debug.Log(line); else Debug.LogWarning(line);
    }

    // Per-frame CSV for diagnosing failures: <project>/Logs/ClimbTest/NN_name.csv
    void WriteTrace(int index, string name)
    {
        if (!writeTraces) return;
        string dir = System.IO.Path.Combine(Application.dataPath, "..", "Logs", "ClimbTest");
        System.IO.Directory.CreateDirectory(dir);
        string file = $"{index:00}_{new string(name.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray())}.csv";
        StringBuilder sb = new StringBuilder("t,phase,x,y,z,climbing,contact,inInk,squid,grounded,ctrl,nx,ny,nz,surfTeam,velY,speed,flags,squidUpY\n");
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        foreach (Frame f in frames)
            sb.AppendLine(string.Format(inv, "{0:F3},{1},{2:F3},{3:F3},{4:F3},{5},{6},{7},{8},{9},{10},{11:F3},{12:F3},{13:F3},{14},{15:F2},{16:F1},{17},{18:F3}",
                f.t, f.phase, f.pos.x, f.pos.y, f.pos.z, B(f.climbing), B(f.contact), B(f.inInk), B(f.squid), B(f.grounded), B(f.controllerOn),
                f.normal.x, f.normal.y, f.normal.z, f.surfaceTeam, f.velY, f.speed, (int)f.flags, (f.squidRot * Vector3.up).y));
        System.IO.File.WriteAllText(System.IO.Path.Combine(dir, file), sb.ToString());
    }

    static int B(bool b) => b ? 1 : 0;

    void OnLog(string message, string stack, LogType type)
    {
        if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert)
            foreach (Lane l in activeLanes) l.errorLogs++; // can't tell which lane logged it
    }

    // ── Scenario helpers ────────────────────────────────────────────────────

    // Holds the given input for `seconds`, recording one Frame per frame under `phase`.
    IEnumerator Hold(Vector2 move, bool swim, float seconds, string phase)
    {
        player.ScriptedInput.move = move;
        player.ScriptedInput.swim = swim;
        int n = Mathf.RoundToInt(seconds / Dt);
        for (int i = 0; i < n; i++)
        {
            yield return null;
            Record(phase);
        }
    }

    // Like Hold, but stops early (after recording) on the first frame where `done` is true.
    IEnumerator HoldUntil(Vector2 move, bool swim, float maxSeconds, string phase, Func<Frame, bool> done)
    {
        player.ScriptedInput.move = move;
        player.ScriptedInput.swim = swim;
        int n = Mathf.RoundToInt(maxSeconds / Dt);
        for (int i = 0; i < n; i++)
        {
            yield return null;
            Record(phase);
            if (done(Last)) yield break;
        }
    }

    // Swims forward into the station's wall until climbing at body height >= y (or 3s pass).
    IEnumerator ClimbTo(float y) => HoldUntil(Fwd, true, 3f, "climb", f => f.climbing && f.pos.y >= y);

    void Record(string phase)
    {
        Transform squid = player.SquidVisual;
        frames.Add(new Frame
        {
            t = frames.Count * Dt,
            pos = station.InverseTransformPoint(player.BodyCenter),
            climbing = player.IsClimbing,
            contact = player.HasWallContact,
            inInk = player.IsInInk,
            squid = player.IsSquid,
            grounded = player.IsGrounded,
            controllerOn = player.ControllerEnabled,
            dead = player.IsDead,
            team = player.Team,
            normal = player.ClimbNormal,
            squidRot = squid != null ? squid.rotation : Quaternion.identity,
            surfaceTeam = player.SurfaceTeam,
            velY = player.VerticalVelocity,
            speed = player.CurrentSpeed,
            flags = player.LastCollisionFlags,
            flying = player.SuperJumpState == SuperJumpPhase.Flying,
            phase = phase,
        });
    }

    void Fail(string msg) { if (!failures.Contains(msg)) failures.Add(msg); }
    void Note(string msg) => notes.Add(msg);

    Frame Last => frames.Count > 0 ? frames[frames.Count - 1] : default;
    List<Frame> Phase(string p) => frames.Where(f => f.phase == p).ToList();
    static float FirstTime(List<Frame> fs, Func<Frame, bool> pred) { foreach (Frame f in fs) if (pred(f)) return f.t; return -1f; }

    // Number of times `pred` changes value across consecutive frames.
    static int Toggles(List<Frame> fs, Func<Frame, bool> pred)
    {
        int n = 0;
        for (int i = 1; i < fs.Count; i++) if (pred(fs[i]) != pred(fs[i - 1])) n++;
        return n;
    }

    // Toggles of `pred` that happen less than `window` seconds after the previous toggle.
    static int RapidToggles(List<Frame> fs, Func<Frame, bool> pred, float window)
    {
        int n = 0; float lastToggle = -999f;
        for (int i = 1; i < fs.Count; i++)
        {
            if (pred(fs[i]) == pred(fs[i - 1])) continue;
            if (fs[i].t - lastToggle < window) n++;
            lastToggle = fs[i].t;
        }
        return n;
    }

    // Longest run of consecutive frames where `pred` holds, in seconds.
    static float LongestRun(List<Frame> fs, Func<Frame, bool> pred)
    {
        int best = 0, cur = 0;
        foreach (Frame f in fs) { cur = pred(f) ? cur + 1 : 0; best = Mathf.Max(best, cur); }
        return best * Dt;
    }

    // Largest frame-to-frame rotation (degrees) of the squid visual among frames matching `pred`.
    static float MaxRotStep(List<Frame> fs, Func<Frame, bool> pred)
    {
        float best = 0f;
        for (int i = 1; i < fs.Count; i++)
            if (pred(fs[i]) && pred(fs[i - 1])) best = Mathf.Max(best, Quaternion.Angle(fs[i].squidRot, fs[i - 1].squidRot));
        return best;
    }

    // Vertical speed (m/s) between the first and last frame of a set.
    static float VerticalSpeed(List<Frame> fs) =>
        fs.Count < 2 ? 0f : (fs[fs.Count - 1].pos.y - fs[0].pos.y) / Mathf.Max(Dt, fs[fs.Count - 1].t - fs[0].t);

    bool RequireClimbing(string when)
    {
        if (Last.climbing) return true;
        Fail($"not climbing {when} (pos {Last.pos:F2}, wall contact {Last.contact})");
        return false;
    }

    // Checked on every frame of every scenario.
    void CheckInvariants()
    {
        for (int i = 0; i < frames.Count; i++)
        {
            Frame f = frames[i];
            if (!f.controllerOn) { Fail($"CharacterController disabled at t={f.t:F2}"); break; }
            if (float.IsNaN(f.pos.x + f.pos.y + f.pos.z)) { Fail($"NaN position at t={f.t:F2}"); break; }
            if (f.climbing && !f.squid) { Fail($"climbing while not in swim form at t={f.t:F2}"); break; }
            if (i > 0)
            {
                // Form changes shift the origin ~0.9m by design.
                bool formChange = f.squid != frames[i - 1].squid;
                if (f.dead != frames[i - 1].dead || f.team != frames[i - 1].team) continue; // respawn teleports
                if (f.flying || frames[i - 1].flying) continue; // Super Jump arcs cover ~1m a frame
                float step = Vector3.Distance(f.pos, frames[i - 1].pos);
                if (step > (formChange ? 1.5f : 0.75f))
                {
                    Fail(step > 5f
                        ? $"teleported {step:F1}m at t={f.t:F2} — probably fell out of the world and respawned"
                        : $"position jumped {step:F2}m in one frame at t={f.t:F2}");
                    break;
                }
            }
        }
    }

    // ── Scenarios ───────────────────────────────────────────────────────────

    IEnumerable<Scenario> Scenarios()
    {
        Scenario S(string name, Station st, Func<IEnumerator> body, string spawn = null) =>
            new Scenario { name = name, station = st, body = body, spawn = spawn };

        yield return S("Lobby: switching form glides the camera instead of snapping", Station.Lobby, CameraFormSwitch);
        yield return S("Lobby: pooled projectiles splat where they land and are reused", Station.Lobby, ProjectilePooling);
        yield return S("Projectiles: speed/gravity curves retime a path without changing it; fast shots can't skip hitboxes", Station.Lobby, ProjectileBallistics);
        yield return S("FlatWall: climb up and over the top", Station.FlatWall, ClimbUpAndOver);
        yield return S("FlatWall: no input on the wall slides slowly without letting go", Station.FlatWall, HoldStillOnWall);
        yield return S("FlatWall: climb down to the floor and swim away", Station.FlatWall, ClimbDownAndAway);
        yield return S("FlatWall: jump while climbing up boosts and stays on the wall", Station.FlatWall, JumpBoost);
        yield return S("FlatWall: jump while descending ejects from the wall", Station.FlatWall, JumpEject);
        yield return S("FlatWall: leaving swim form on the wall drops cleanly to the floor", Station.FlatWall, LeaveSwimOnWall);
        yield return S("Ledge: swimming off a ledge falls past the inked wall below", Station.Ledge, LedgeWalkOff);
        yield return S("Pillar: strafing around an outer corner keeps climbing", Station.Pillar, OuterCorner);
        yield return S("InnerCorner: strafing into an inner corner transfers to the side wall", Station.InnerCorner, InnerCorner);
        yield return S("NeutralWall: an unpainted wall can't be climbed", Station.NeutralWall, CannotClimb);
        yield return S("EnemyWall: an enemy-ink wall can't be climbed", Station.EnemyWall, CannotClimb);
        yield return S("PartialWall: reaching the top of own ink pops off, then re-grabs after 0.2s", Station.PartialWall, PartialWallPopOff);
        yield return S("PartialWall: holding up bobs at the ink edge without climbing past it", Station.PartialWall, PartialWallBob);
        yield return S("Ramp30: swims up a walkable slope without climb mode or jitter", Station.Ramp30, ShallowRampUp);
        yield return S("Ramp30: swims down a walkable slope without bouncing", Station.Ramp30, ShallowRampDown, "SpawnTop");
        yield return S("Ramp60: a steep slope is climbed", Station.Ramp60, SteepRamp);
        yield return S("Combat: replays are visual-only, hits route to the victim, damage kills and respawns", Station.Lobby, Combat);
        yield return S("Death: the camera circles the spot while a popup says who and with what; back at spawn 4s later, held in swim form for 1s; falling out of bounds too", Station.Lobby, Death);
        yield return S("HostMenu: percentages toggle, special fills, team editor moves players between teams and the bench", Station.Lobby, HostMenuTeams);
        yield return S("Map: holding opens it, lists our team with lines to markers, picking a teammate requests a Super Jump", Station.Lobby, MapTeam);
        yield return S("SuperJump: locked charge in swim form, high arc onto the teammate, unlocks on landing (swim kept only if held)", Station.Lobby, SuperJump);
        yield return S("SuperJump: the landing spot locks on click, only our death cancels, spawn is always a target", Station.Lobby, SuperJumpLocking);
        yield return S("Sub: a beacon costs 70% ink, shows on our map only, and a Super Jump onto it breaks it", Station.Lobby, SubBeacon);
        yield return S("Beacon: 35 health from enemy fire only, expires, remote hits go to the owner", Station.Lobby, BeaconHealth);
        yield return S("Special: charges from new turf and damage dealt; bubble shield refills ink and blocks damage for 5s", Station.Lobby, SpecialShield);
        yield return S("Loadout HUD: gauges follow ink and special charge, READY when full", Station.Lobby, LoadoutGauges);
        yield return S("Special: half lost on death; charged state is synced and shown on the player bar", Station.Lobby, SpecialDeathAndSync);
        yield return S("Lobby: swimming is fastest in our ink, slower on bare ground, slowest in enemy ink", Station.Lobby, SwimSpeeds);
        yield return S("LobbyMenu: hidden in dev mode, lists lobbies as rows, a failed join reports and refreshes", Station.Lobby, LobbyMenuList);
        yield return S("Colours: a random pair from the set each load, the host's pick reaches everyone and recolours ink, players and HUD", Station.Lobby, TeamColourPairs);
        yield return S("Match: countdown locks and resets everyone, final minute hides the bar (no banner), results fill to a suspense point then the real split", Station.Lobby, MatchFlow);
        yield return S("Match: a mid-match joiner spectates the rest of the round, then gets their team back", Station.Lobby, LateJoin);
        yield return S("Spectating: no player of our own means the overhead view", Station.Lobby, SpectatorView);
        yield return S("Weapon: holding fire also drops 12-18 ink at our feet every 0.4s", Station.Lobby, FeetInk);
        yield return S("Weapons: Inkshot fires slower, further and tighter than Airspray SE", Station.Lobby, WeaponVariety);
        yield return S("Weapons: prefabs swapped in our hands, synced to remote copies; jumping doesn't carry into shots", Station.Lobby, WeaponPrefabs);
        yield return S("LoadoutMenu: picks the weapon and sub, applies and remembers them, can't open mid-match", Station.Lobby, LoadoutMenuPicks);
        yield return S("InkStrike: aimed on the map (cancel keeps the charge), marked, then inks the whole column down to the ground; lethal core, non-fatal edge", Station.Lobby, InkStrikeSpecial);
        yield return S("Sprinkler: thrown, sticks, sprays both ways and winds down; one at a time; 30 HP; remote copies follow the owner", Station.Lobby, SprinklerSub);
        yield return S("HUD: rosters, death marks, match timer and turf bar track the game", Station.Lobby, Hud);
    }

    IEnumerator CameraFormSwitch()
    {
        // Each form switch moves the origin ~0.9m; the camera rig must ease across it.
        Transform rig = player.CameraRig;
        if (rig == null) { Fail("player has no camera rig"); yield break; }
        var heights = new List<float> { rig.position.y };
        var phases = new[] { (true, "to squid"), (false, "to kid"), (true, "to squid again") };
        foreach (var (swim, label) in phases)
        {
            player.ScriptedInput.swim = swim;
            float maxStep = 0f, start = rig.position.y;
            for (int i = 0; i < 30; i++) // 0.5s
            {
                yield return null;
                Record(label);
                heights.Add(rig.position.y);
                maxStep = Mathf.Max(maxStep, Mathf.Abs(heights[heights.Count - 1] - heights[heights.Count - 2]));
            }
            float moved = Mathf.Abs(rig.position.y - start);
            Note($"{label}: camera moved {moved:F2}m, largest single-frame step {maxStep:F3}m");
            if (moved < 0.5f) Fail($"{label}: camera didn't follow the form change (moved {moved:F2}m)");
            if (maxStep > 0.2f) Fail($"{label}: camera snapped {maxStep:F2}m in one frame");
            float settle = Mathf.Abs(heights[heights.Count - 1] - heights[heights.Count - 2]);
            if (settle > 0.005f) Fail($"{label}: camera still moving after 0.5s ({settle:F3}m/frame)");
        }
    }

    IEnumerator ProjectilePooling()
    {
        if (projectilePrefab == null) { Fail("runner has no projectile prefab assigned (rebuild the test scene)"); yield break; }

        // Three volleys of 6 team-2 shots straight down onto the floor, half with hidden visuals.
        int before = ProjectileManager.VisualsCreated, afterFirst = 0;
        var targets = new List<Vector3>();
        for (int volley = 0; volley < 3; volley++)
        {
            for (int i = 0; i < 6; i++)
            {
                Vector3 p = station.TransformPoint(new Vector3(-6f + i * 2.4f, 3f, 4f + volley * 2.5f));
                targets.Add(new Vector3(p.x, station.position.y, p.z));
                ProjectileManager.Fire(projectilePrefab, p, Vector3.down * 8f, 10, 2, visible: i % 2 == 0);
            }
            yield return Hold(Still, false, 0.6f, "volley");
            if (volley == 0) afterFirst = ProjectileManager.VisualsCreated - before;
        }
        yield return Hold(Still, false, 0.3f, "settle");

        int created = ProjectileManager.VisualsCreated - before, stillFlying = ProjectileManager.LiveCount;
        Note($"{targets.Count} shots used {created} visuals");
        if (afterFirst == 0 || afterFirst > 3) Fail($"expected a visual per visible shot of the first volley, got {afterFirst}");
        if (created > afterFirst) Fail($"visuals weren't reused ({afterFirst} after the first volley, {created} after three)");
        if (stillFlying > 0) Fail($"{stillFlying} shot(s) never finished");

        int painted = 0;
        foreach (Vector3 t in targets)
            if (Physics.Raycast(t + Vector3.up, Vector3.down, out RaycastHit hit, 2f, PhysicsLayers.Environment, QueryTriggerInteraction.Ignore)
                && hit.collider.TryGetComponent(out SurfaceInkManager ink) && ink.getSurfaceTeam(hit.textureCoord) == 2)
                painted++;
        if (painted < targets.Count) Fail($"only {painted}/{targets.Count} shots painted where they landed");
    }

    IEnumerator TeamColourPairs()
    {
        NetGameManager gm = NetGameManager.Instance;
        MatchHUD hud = FindFirstObjectByType<MatchHUD>();
        SurfaceInkManager floor = station.GetComponentsInChildren<SurfaceInkManager>().First(m => m.name == "Floor");
        Material ink = floor.GetComponent<Renderer>().material;
        int start = gm.ColourPair;
        Note($"{gm.ColourPairCount} colour pairs; this load picked {start} ({gm.ColourPairName})");
        if (gm.ColourPairCount < 5) Fail("expected a set of colour pairs");
        if (start < 0 || start >= gm.ColourPairCount) Fail("no colour pair picked on load");

        // The host's roster carries its pick; everything recolours.
        int other = (start + 1) % gm.ColourPairCount;
        int changes = 0;
        System.Action counted = () => changes++;
        NetGameManager.TeamColoursChanged += counted;
        gm.Receive(NetMsg.TeamAssign, new TeamAssignData { ids = new ulong[0], roles = new int[0], colourPair = other }, 0);
        yield return Hold(Still, false, 0.2f, "colours");
        NetGameManager.TeamColoursChanged -= counted;
        bool inkOk = ink.GetColor("_AlphaColor") == gm.AlphaTeam && ink.GetColor("_BetaColor") == gm.BetaTeam;
        Color own = player.Team == 2 ? gm.BetaTeam : gm.AlphaTeam;
        bool hudOk = hud == null || hud.AlphaBarColour == gm.AlphaTeam;
        Note($"after the host's roster: pair {gm.ColourPair} ({gm.ColourPairName}), {changes} change event(s); ink {(inkOk ? "recoloured" : "stale")}, HUD bar {(hudOk ? "recoloured" : "stale")}, our colour {own}");
        if (gm.ColourPair != other || changes != 1) Fail("the host's colour pick wasn't applied (once)");
        if (!inkOk) Fail("ink surfaces kept the old colours");
        if (!hudOk) Fail("the turf bar kept the old colours");
        if (player.TeamColour != own) Fail("our player kept the old colour");

        gm.SetColourPair(start);
        yield return Hold(Still, false, 0.1f, "colours");
    }

    IEnumerator Death()
    {
        NetGameManager gm = NetGameManager.Instance;
        DeathPopup popup = FindFirstObjectByType<DeathPopup>(FindObjectsInactive.Include);
        Camera view = player.CameraRig != null ? player.CameraRig.GetComponentInChildren<Camera>(true) : null;
        if (popup == null || view == null) { Fail("no DeathPopup in the HUD (or no player camera)"); yield break; }
        Transform cam = view.transform;
        const ulong FoeId = 3070;
        int enemyTeam = player.Team == 1 ? 2 : 1;
        SpawnRemote(FoeId, enemyTeam, station.TransformPoint(new Vector3(8f, 0.05f, 8f)), "Splatterer");
        player.Hitbox.ResetHealth();
        yield return Hold(Still, false, 0.3f, "stand");

        // Splatted by them: the camera circles where it happened, the popup says who and with what.
        Vector3 spot = player.transform.position;
        gm.Receive(NetMsg.Damage, new DamageData { targetSteamId = player.OwnerId, attackerSteamId = FoeId, amount = 250f, fromTeam = enemyTeam, source = "Inkshot" }, FoeId);
        yield return HoldUntil(Still, false, 0.5f, "dead", f => f.dead);
        float diedAt = Time.time;
        yield return null;
        Note($"popup: \"{popup.Message}\"");
        if (!player.IsDead || player.KilledBy != FoeId || player.KilledWith != "Inkshot") Fail("the hit didn't splat us (or lost who and what did it)");
        if (!popup.Shown || !popup.Message.Contains("Splatterer") || !popup.Message.Contains("Inkshot")) Fail("the popup doesn't say who splatted us and with what");
        float Bearing() { Vector3 d = cam.position - player.DeathSpot; return Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg; }
        float startBearing = Bearing();
        yield return Hold(Still, false, 1f, "dead");
        Vector3 offset = cam.position - player.DeathSpot;
        float turned = Mathf.Abs(Mathf.DeltaAngle(startBearing, Bearing()));
        Note($"death camera {new Vector2(offset.x, offset.z).magnitude:F1}m out and {offset.y:F1}m up, turned {turned:F0}° in 1s");
        if (Vector3.Distance(player.DeathSpot, spot) > 0.5f) Fail("the death camera isn't over where we were splatted");
        if (!player.DeathCamera || new Vector2(offset.x, offset.z).magnitude < 3f || offset.y < 1f || turned < 20f) Fail("the camera isn't circling over the spot");

        // Back at a spawn after 4s, in swim form and held still (even pushing forward) for a second.
        yield return HoldUntil(Fwd, false, 4.5f, "dead", f => !f.dead);
        float respawnAfter = Time.time - diedAt;
        frames.Clear(); // the respawn is a teleport
        Vector3 at = player.transform.position;
        float toSpawn = GameObject.FindGameObjectsWithTag(player.Team == 1 ? "AlphaSpawn" : "BetaSpawn")
            .Select(g => Vector3.Distance(g.transform.position, at)).DefaultIfEmpty(Vector3.Distance(player.SpawnPosition, at)).Min();
        if (Mathf.Abs(respawnAfter - 4f) > 0.15f) Fail($"respawned {respawnAfter:F2}s after dying, not 4s");
        if (toSpawn > 1.5f) Fail($"respawned {toSpawn:F1}m from the nearest spawn");
        yield return Hold(Fwd, false, 0.6f, "held");
        float drift = Vector3.ProjectOnPlane(player.transform.position - at, Vector3.up).magnitude;
        bool heldSquid = player.IsSquid, holding = player.IsRespawning, popupGone = !popup.Shown;
        yield return Hold(Fwd, false, 0.8f, "free");
        float moved = Vector3.ProjectOnPlane(player.transform.position - at, Vector3.up).magnitude;
        Note($"respawned {respawnAfter:F2}s after dying; held {(heldSquid ? "in swim form" : "standing")}, drifted {drift:F2}m in 0.6s; then moved {moved:F1}m");
        if (!holding || !heldSquid || drift > 0.1f) Fail("not held still in swim form just after respawning");
        if (!popupGone) Fail("the popup is still up after respawning");
        if (player.IsRespawning || moved < 0.5f) Fail("didn't get control back after the hold");

        // Falling out of the world: the same, the camera over the last ground we stood on.
        player.PlaceAt(station.TransformPoint(new Vector3(-6f, 0.1f, -6f)), player.transform.eulerAngles.y);
        yield return Hold(Still, false, 0.3f, "ground");
        Vector3 ground = player.transform.position;
        player.transform.position = new Vector3(ground.x, PlayerController.KillHeight - 2f, ground.z);
        Physics.SyncTransforms();
        yield return HoldUntil(Still, false, 0.5f, "fall", f => f.dead);
        frames.Clear(); // the drop is a teleport
        yield return null;
        Note($"fell: \"{popup.Message}\", camera over a spot {Vector3.Distance(player.DeathSpot, ground):F2}m from the last ground");
        if (!player.IsDead || player.KilledBy != 0 || player.KilledWith != PlayerController.FellCause) Fail("falling out of bounds didn't splat us");
        if (!popup.Shown || !popup.Message.Contains(PlayerController.FellCause)) Fail("the popup doesn't say we fell");
        if (Vector3.Distance(player.DeathSpot, ground) > 0.5f) Fail("the death camera isn't over where we fell from");
        player.Respawn();
        yield return Hold(Still, false, 0.2f, "settle");
        frames.Clear();
        if (player.DeathCamera || player.IsDead) Fail("still splatted after respawning");

        Despawn(FoeId);
        yield return Hold(Still, false, 0.1f, "cleanup");
    }

    IEnumerator ProjectileBallistics()
    {
        NetGameManager gm = NetGameManager.Instance;
        CaptureSends();
        const ulong FoeId = 3060;
        int enemyTeam = player.Team == 1 ? 2 : 1;

        // Speed Ã—2 with gravity Ã—4 follows the same path in half the time.
        var landed = new Dictionary<ProjectileManager.Shot, (Vector3 point, float age)>();
        Action<ProjectileManager.Shot, Vector3> onImpact = (shot, point) => landed[shot] = (point, shot.age);
        ProjectileManager.Impact += onImpact;
        Vector3 from = station.TransformPoint(new Vector3(-8f, 2.5f, -8f));
        Vector3 launch = station.TransformDirection(new Vector3(0f, 0.3f, 1f)).normalized * 8f;
        var doubled = new Ballistics { velocityOverTime = AnimationCurve.Constant(0f, 1f, 2f), gravityOverTime = AnimationCurve.Constant(0f, 1f, 4f) };
        ProjectileManager.Shot plain = ProjectileManager.Fire(projectilePrefab, from, launch, 10, player.Team, ownerId: player.OwnerId);
        ProjectileManager.Shot quick = ProjectileManager.Fire(projectilePrefab, from + station.TransformDirection(Vector3.right) * 3f, launch, 10, player.Team, ownerId: player.OwnerId, ballistics: doubled);
        yield return HoldUntil(Still, false, 3f, "fly", f => landed.ContainsKey(plain) && landed.ContainsKey(quick));
        ProjectileManager.Impact -= onImpact;
        if (!landed.ContainsKey(plain) || !landed.ContainsKey(quick)) { Fail("shots didn't land"); NetGameManager.SendOverride = null; yield break; }
        Vector3 offset = station.TransformDirection(Vector3.right) * 3f;
        float pathError = Vector3.Distance(landed[plain].point + offset, landed[quick].point);
        Note($"plain landed after {landed[plain].age:F2}s, doubled after {landed[quick].age:F2}s, {pathError:F2}m apart (same path)");
        if (pathError > 0.25f) Fail("doubling speed and quadrupling gravity changed the path");
        if (Mathf.Abs(landed[quick].age / landed[plain].age - 0.5f) > 0.05f) Fail("doubled shot didn't take half the time");

        // A very fast shot still can't skip past a hitbox between frames.
        SpawnRemote(FoeId, enemyTeam, station.TransformPoint(new Vector3(8f, 0.05f, 8f)), "Foe");
        yield return Hold(Still, false, 0.2f, "fast");
        PlayerController foe = gm.GetPlayer(FoeId);
        sent.Clear();
        Vector3 across = station.TransformDirection(Vector3.right);
        ProjectileManager.Fire(projectilePrefab, foe.BodyCenter - across * 12f, across * 150f, 10, player.Team, ownerId: player.OwnerId,
                               ballistics: new Ballistics { gravityOverTime = AnimationCurve.Constant(0f, 1f, 0f) });
        yield return Hold(Still, false, 0.3f, "fast");
        Note($"150 m/s shot: {(sent.Any(m => m.id == NetMsg.Damage && m.to == FoeId) ? "hit" : "missed")} (moves 2m per frame)");
        if (!sent.Any(m => m.id == NetMsg.Damage && m.to == FoeId)) Fail("a 150 m/s shot passed through the hitbox");

        // Grates let ink through (the Projectile layer ignores MapGrate in the physics matrix).
        GameObject grate = GameObject.CreatePrimitive(PrimitiveType.Cube);
        grate.layer = LayerMask.NameToLayer("MapGrate");
        Vector3 under = station.TransformPoint(new Vector3(-8f, 0f, 4f));
        grate.transform.position = under + Vector3.up * 1.5f;
        grate.transform.localScale = new Vector3(3f, 0.1f, 3f);
        Physics.SyncTransforms();
        Vector3? throughGrate = null;
        Action<ProjectileManager.Shot, Vector3> onGrate = (shot, point) => throughGrate = point;
        ProjectileManager.Impact += onGrate;
        ProjectileManager.Fire(projectilePrefab, under + Vector3.up * 3f, Vector3.down * 8f, 10, player.Team, ownerId: player.OwnerId);
        yield return HoldUntil(Still, false, 1f, "grate", f => throughGrate.HasValue);
        ProjectileManager.Impact -= onGrate;
        Destroy(grate);
        Note($"shot dropped through a grate 1.5m up: {(throughGrate.HasValue ? $"landed {throughGrate.Value.y - under.y:F2}m above the floor" : "never landed")}");
        if (!throughGrate.HasValue || throughGrate.Value.y > under.y + 0.5f) Fail("a shot stopped on a grate instead of going through");

        Despawn(FoeId);
        yield return Hold(Still, false, 0.1f, "fast");
        NetGameManager.SendOverride = null;
    }

    IEnumerator ClimbUpAndOver()
    {
        // Stop once on top, or the player swims off the far side.
        yield return HoldUntil(Fwd, true, 8f, "climb", f => f.pos.y > WallHeight - 0.3f && f.pos.z > 0.5f && f.grounded);
        yield return Hold(Still, true, 0.5f, "settle");

        float engage = FirstTime(frames, f => f.climbing);
        if (engage < 0f || engage > 2f) Fail($"climbing didn't engage within 2s (first climbing frame t={engage:F2})");

        float maxY = frames.Max(f => f.pos.y);
        if (maxY < WallHeight - 0.5f) Fail($"never reached the top (max height {maxY:F2} of {WallHeight})");

        Frame last = Last;
        if (last.climbing) Fail("still in climb mode at the end (should be on top of the block)");
        if (!(last.pos.y > WallHeight - 0.2f && last.pos.z > 0.3f)) Fail($"didn't end up on top of the block (end pos {last.pos:F2})");

        int toggles = Toggles(frames, f => f.climbing);
        if (toggles > 2) Fail($"climb state toggled {toggles} times (expected once on, once off)");
        int inkToggles = Toggles(frames, f => f.inInk);
        if (inkToggles > 2) Fail($"squid hide/show (IsInInk) toggled {inkToggles} times");

        // Reaching the top should pop off the lip, not switch straight to swimming.
        int lastClimb = frames.FindLastIndex(f => f.climbing);
        if (lastClimb >= 0 && lastClimb < frames.Count - 1)
        {
            List<Frame> after = frames.Skip(lastClimb + 1).ToList();
            float hop = after.Max(f => f.pos.y) - WallHeight;
            Note($"popped {hop:F2}m above the top");
            // Loose bounds so they don't encode the tuned top-of-wall lift.
            if (hop < 0.05f) Fail($"didn't pop up off the top of the wall (peak {hop:F2}m above the lip)");
            if (hop > 1.0f) Fail($"launched off the top of the wall ({hop:F2}m above the lip)");
            List<Frame> early = after.Where(f => f.t <= after[0].t + 0.1f).ToList();
            float hSpeed = early.Count > 1
                ? Vector2.Distance(new Vector2(early.Last().pos.x, early.Last().pos.z), new Vector2(early[0].pos.x, early[0].pos.z)) / (early.Last().t - early[0].t)
                : 0f;
            Note($"horizontal speed just after the pop {hSpeed:F1} m/s");
            if (hSpeed > 6f) Fail($"snapped to full swim speed straight after leaving the wall ({hSpeed:F1} m/s)");
        }

        List<Frame> mid = frames.Where(f => f.climbing && f.pos.y > 1f && f.pos.y < WallHeight - 1f).ToList();
        float speed = VerticalSpeed(mid);
        Note($"climb speed {speed:F1} m/s");
        // climbSpeedFactor (0.8) x 12 m/s, less the gravity slide.
        if (mid.Count > 1 && (speed < 8f || speed > 10.5f)) Fail($"unboosted climb speed is {speed:F1} m/s (expected ~9.6)");

        float rot = MaxRotStep(frames, f => f.climbing && f.pos.y > 1f && f.pos.y < WallHeight - 1f);
        Note($"max squid rotation step {rot:F1}°/frame");
        if (rot > 3f) Fail($"squid visual jitters up to {rot:F1}°/frame on a flat wall");

        float drift = frames.Where(f => f.climbing).Select(f => Mathf.Abs(f.pos.x)).DefaultIfEmpty(0f).Max();
        if (drift > 0.5f) Fail($"drifted {drift:F2}m sideways while climbing straight up");
    }

    IEnumerator HoldStillOnWall()
    {
        yield return ClimbTo(2.5f);
        if (!RequireClimbing("after swimming into the wall")) yield break;

        // The slide is slow (~0.5 m/s), so allow plenty of time to reach the base.
        yield return HoldUntil(Still, true, 10f, "hold", f => !f.climbing && f.pos.y < 0.5f);
        yield return Hold(Still, true, 0.3f, "hold");
        List<Frame> hold = Phase("hold");
        List<Frame> above = hold.Where(f => f.pos.y > 0.8f).ToList();

        float letGo = FirstTime(above, f => !f.climbing);
        if (letGo >= 0f) Fail($"let go of the wall mid-height at t={letGo:F2}");
        float hidden = FirstTime(above, f => !f.inInk);
        if (hidden >= 0f) Fail($"squid un-hid (IsInInk false) while on the wall at t={hidden:F2}");

        float slide = -VerticalSpeed(above);
        Note($"slide speed {slide:F2} m/s");
        if (slide > 3f) Fail($"slides down too fast with no input ({slide:F2} m/s)");

        if (Last.climbing) Fail($"still in climb mode at the end (pos {Last.pos:F2}) — should release at the wall's base");
    }

    IEnumerator ClimbDownAndAway()
    {
        yield return ClimbTo(4f);
        if (!RequireClimbing("after swimming into the wall")) yield break;

        yield return HoldUntil(Back, true, 6f, "down", f => f.pos.z < -2.5f); // well clear, but still on the floor
        List<Frame> down = Phase("down");
        float floorT = FirstTime(down, f => f.pos.y < 0.5f);
        if (floorT < 0f) { Fail($"never got back down to the floor (lowest {down.Min(f => f.pos.y):F2})"); yield break; }

        float stuck = FirstTime(down, f => f.t > floorT + 0.5f && f.climbing);
        if (stuck >= 0f) Fail($"still in climb mode on the floor at t={stuck:F2}");
        float both = LongestRun(down, f => f.climbing && f.grounded);
        if (both > 0.25f) Fail($"climbing and grounded at the same time for {both:F2}s");
        if (Last.pos.z > -1f) Fail($"didn't swim away from the wall after reaching the floor (end z={Last.pos.z:F2})");
    }

    IEnumerator JumpBoost()
    {
        yield return ClimbTo(1.5f);
        if (!RequireClimbing("after swimming into the wall")) yield break;

        yield return Hold(Fwd, true, 0.25f, "base");
        // Spam jump: a press every 0.1s.
        for (int i = 0; i < 5; i++)
        {
            player.PressJump();
            yield return Hold(Fwd, true, 0.1f, "boost");
        }

        float baseSpeed  = VerticalSpeed(Phase("base"));
        List<Frame> boost = Phase("boost");
        float boostSpeed = VerticalSpeed(boost.Skip(boost.Count / 2).ToList()); // second half, once built up
        Note($"climb speed {baseSpeed:F1} m/s normally, {boostSpeed:F1} m/s spamming jump");

        float off = FirstTime(boost, f => !f.climbing && f.pos.y < WallHeight - 0.5f);
        if (off >= 0f) Fail($"jump knocked the player off the wall at t={off:F2}");
        if (boostSpeed < 11.3f) Fail($"spamming jump only reached {boostSpeed:F1} m/s (expected ~12, full swim speed)");
        if (boostSpeed < baseSpeed + 1.5f) Fail($"jump boost had little effect ({baseSpeed:F1} â†’ {boostSpeed:F1} m/s)");
    }

    IEnumerator JumpEject()
    {
        yield return ClimbTo(5f);
        if (!RequireClimbing("after swimming into the wall")) yield break;
        yield return Hold(Back, true, 0.6f, "descend"); // long enough for the smoothed input to reverse
        if (!RequireClimbing("while descending")) yield break;

        player.PressJump();
        yield return HoldUntil(Back, true, 2f, "eject", f => f.pos.z < -2.5f && f.grounded);
        List<Frame> ej = Phase("eject");
        float start = ej[0].t;

        float detach = FirstTime(ej, f => !f.climbing);
        if (detach < 0f || detach - start > 0.1f) Fail($"didn't leave climb mode right after jumping (detached at +{detach - start:F2}s)");
        else
        {
            float regrab = FirstTime(ej, f => f.t > detach && f.t < detach + 0.3f && f.climbing);
            if (regrab >= 0f) Fail($"re-grabbed the wall {regrab - detach:F2}s after ejecting");
        }

        float away = ej.Max(f => -f.pos.z);
        Note($"max distance from wall {away:F2}m");
        if (away < 1f) Fail($"eject didn't carry the player away from the wall (max {away:F2}m)");
        if (Last.climbing) Fail("back in climb mode at the end");
    }

    IEnumerator LeaveSwimOnWall()
    {
        yield return ClimbTo(2f);
        if (!RequireClimbing("after swimming into the wall")) yield break;
        float height = Last.pos.y;

        yield return Hold(Still, false, 2.5f, "unswim");
        Note($"left swim form at height {height:F2}");
        // Standing body centre is ~1m above the floor.
        if (Last.pos.y > 1.3f) Fail($"didn't drop to the floor (stuck at y={Last.pos.y:F2})");
        if (Last.pos.z > -0.45f) Fail($"standing capsule left overlapping the wall (centre z={Last.pos.z:F2}, radius 0.5)");
    }

    IEnumerator LedgeWalkOff()
    {
        yield return HoldUntil(Fwd, true, 3f, "off", f => f.pos.z < -2.5f && f.grounded);
        float grab = FirstTime(frames, f => f.climbing);
        if (grab >= 0f)
        {
            Frame g = frames.First(f => f.climbing);
            Fail($"grabbed the wall below the ledge at t={grab:F2} (y={g.pos.y:F2})");
        }
        if (!(Last.pos.y < 0.6f && Last.pos.z < -1f)) Fail($"didn't land on the floor beyond the ledge (end pos {Last.pos:F2})");

        // Swimming off the edge should carry the swim speed into the fall, not drop to a crawl.
        int leave = frames.FindIndex(f => !f.grounded && f.pos.y < LedgeHeight);
        if (leave > 0)
        {
            List<Frame> fall = frames.Skip(leave).Where(f => f.t <= frames[leave].t + 0.15f).ToList();
            float hSpeed = fall.Count > 1 ? Mathf.Abs(fall.Last().pos.z - fall[0].pos.z) / (fall.Last().t - fall[0].t) : 0f;
            Note($"horizontal speed leaving the ledge {hSpeed:F1} m/s");
            if (hSpeed < 9f) Fail($"lost speed swimming off the ledge ({hSpeed:F1} m/s; swimming in own ink is ~12)");
        }
    }

    IEnumerator OuterCorner()
    {
        yield return ClimbTo(2f);
        if (!RequireClimbing("after swimming into the pillar")) yield break;

        yield return Hold(new Vector2(1f, 0.2f), true, 2f, "strafe");
        List<Frame> st = Phase("strafe");

        bool onSide = st.Any(f => f.climbing && Vector3.Dot(f.normal, Vector3.right) > 0.9f);
        if (!onSide) Fail($"never moved onto the side face (max x {st.Max(f => f.pos.x):F2}; corner at x={PillarWidth / 2f})");
        float fell = FirstTime(st, f => f.pos.y < 0.6f);
        if (fell >= 0f) Fail($"fell off the pillar at t={fell:F2}");
        float lost = LongestRun(st, f => !f.contact);
        if (lost > 0.15f) Fail($"lost the wall for {lost:F2}s going around the corner");
    }

    IEnumerator InnerCorner()
    {
        yield return ClimbTo(2f);
        if (!RequireClimbing("after swimming into the wall")) yield break;

        // Stop once well along the side wall (it ends at z = -6).
        yield return HoldUntil(new Vector2(1f, 0.2f), true, 3f, "strafe", f => f.pos.z < -2.5f);
        List<Frame> st = Phase("strafe");

        bool onSide = st.Any(f => f.climbing && Vector3.Dot(f.normal, Vector3.left) > 0.9f);
        if (!onSide) Fail("never transferred onto the side wall");
        else if (Last.pos.z > -1f) Fail($"stuck in the corner instead of continuing along the side wall (end pos {Last.pos:F2})");
        float fell = FirstTime(st, f => f.pos.y < 0.6f);
        if (fell >= 0f) Fail($"fell off at t={fell:F2}");
    }

    IEnumerator CannotClimb()
    {
        yield return Hold(Fwd, true, 3f, "push");
        float climbed = FirstTime(frames, f => f.climbing);
        if (climbed >= 0f) Fail($"entered climb mode at t={climbed:F2}");
        float maxY = frames.Where(f => f.t > 0.3f).Max(f => f.pos.y); // skip the switch into swim form
        if (maxY > 1f) Fail($"rose to y={maxY:F2} against the wall");
    }

    // Climbs the half-inked wall until climbing ends (popped off the top of the ink).
    IEnumerator ClimbUntilPopOff()
    {
        bool climbed = false;
        yield return HoldUntil(Fwd, true, 3f, "climb", f => (climbed |= f.climbing) && !f.climbing);
    }

    bool CheckPopOff()
    {
        if (FirstTime(frames, f => f.climbing) < 0f) { Fail("never engaged climbing on the inked lower half"); return false; }
        if (Last.climbing) { Fail($"never popped off (still climbing at y={Last.pos.y:F2})"); return false; }
        float topClimbing = frames.Where(f => f.climbing).Max(f => f.pos.y);
        Note($"popped off at {topClimbing:F2} (ink ends at {PartialInkHeight})");
        if (topClimbing > PartialInkHeight + 0.4f) Fail($"climbed to {topClimbing:F2} before popping off, past the top of the ink ({PartialInkHeight})");
        return true;
    }

    IEnumerator PartialWallPopOff()
    {
        yield return ClimbUntilPopOff();
        if (!CheckPopOff()) yield break;
        float popT = Last.t, popY = Last.pos.y;

        // Keep holding up: pop off, fall through the no-swim window, then re-grab the ink.
        yield return HoldUntil(Fwd, true, 2f, "after", f => f.climbing || f.grounded);
        List<Frame> after = Phase("after");

        float early = FirstTime(after, f => f.climbing && f.t < popT + 0.2f - 0.001f);
        if (early >= 0f) Fail($"re-grabbed {early - popT:F2}s after popping off, inside the 0.2s no-swim window");

        float peak = after.Max(f => f.pos.y);
        Note($"popped off at {popY:F2}, peaked at {peak:F2}");
        // Ink-edge pop keeps only a little upward speed.
        if (peak < popY + 0.01f) Fail($"popping off didn't keep any upward momentum (peak {peak - popY:F2}m above the pop)");
        if (peak > popY + 0.3f) Fail($"popped too high off the edge of the ink ({peak - popY:F2}m above the pop)");

        if (!Last.climbing) Fail($"didn't swim back into the wall's ink after the no-swim window (end pos {Last.pos:F2}, grounded {Last.grounded})");
        else Note($"re-grabbed {Last.t - popT:F2}s after popping off, at y={Last.pos.y:F2}");
    }

    IEnumerator PartialWallBob()
    {
        // Holding up at the ink edge bobs: pop off, re-grab below, repeat. Exit splashes creep the
        // edge up a little each time; it must never re-grab early or jump past a splash's reach.
        yield return Hold(Fwd, true, 4f, "bob");
        if (FirstTime(frames, f => f.climbing) < 0f) { Fail("never engaged climbing on the inked lower half"); yield break; }
        if (frames.Where(f => f.t > 1f).Any(f => f.grounded)) Fail("fell all the way to the floor instead of re-grabbing the ink below");

        var pops = new List<float>();
        float offSince = -1f, segTop = float.MinValue, shortestGap = float.MaxValue;
        for (int i = 1; i < frames.Count; i++)
        {
            Frame a = frames[i - 1], b = frames[i];
            if (b.climbing) segTop = Mathf.Max(segTop, b.pos.y);
            if (a.climbing && !b.climbing) { pops.Add(segTop); segTop = float.MinValue; offSince = b.t; }
            if (!a.climbing && b.climbing && offSince >= 0f) shortestGap = Mathf.Min(shortestGap, b.t - offSince);
        }
        if (shortestGap < 0.2f - 0.001f) Fail($"re-grabbed only {shortestGap:F2}s after popping off (no-swim window is 0.2s)");
        for (int i = 1; i < pops.Count; i++)
            if (pops[i] - pops[i - 1] > 0.5f)
                Fail($"pop-off {i + 1} was {pops[i] - pops[i - 1]:F2}m above the previous one (more than an exit splash can paint)");
        if (pops.Count > 0)
            Note($"{pops.Count} pop-offs, first at {pops[0]:F2}, last at {pops[pops.Count - 1]:F2} (ink edge creeps via exit splashes)");
    }

    IEnumerator ShallowRampUp()
    {
        // Stop short of the top so the run doesn't sail off the end.
        float topY = RampSurfacePoint(30f, Ramp30Length, Ramp30Length).y;
        yield return HoldUntil(Fwd, true, 3f, "up", f => f.pos.y > topY - 0.8f);
        float climbed = FirstTime(frames, f => f.climbing);
        if (climbed >= 0f) Fail($"entered climb mode on a walkable slope at t={climbed:F2}");

        float rise = Last.pos.y - frames[0].pos.y;
        if (rise < 2f) Fail($"didn't make it up the slope (rose {rise:F2}m)");

        List<Frame> onRamp = frames.Where(f => f.pos.z > 0.5f && f.pos.y > 0.4f).ToList();
        if (onRamp.Count > 0)
        {
            float inInk = onRamp.Count(f => f.inInk) / (float)onRamp.Count;
            if (inInk < 0.95f) Fail($"only in ink {inInk:P0} of the time on a fully inked slope");
            int dips = 0;
            for (int i = 1; i < onRamp.Count; i++) if (onRamp[i].pos.y < onRamp[i - 1].pos.y - 0.005f) dips++;
            if (dips > 2) Fail($"y-axis jitter going up the slope ({dips} downward steps)");

            Vector3 rampUp = RampRotation(30f) * Vector3.up;
            float align = onRamp.Average(f => Vector3.Dot(f.squidRot * Vector3.up, rampUp));
            Note($"squid/slope alignment {align:F3}");
            if (align < 0.95f) Fail($"squid isn't aligned to the slope (avg up·normal {align:F2})");
        }
    }

    IEnumerator ShallowRampDown()
    {
        yield return HoldUntil(Fwd, true, 2.5f, "down", f => f.pos.z < -2f);
        float climbed = FirstTime(frames, f => f.climbing);
        if (climbed >= 0f) Fail($"entered climb mode on a walkable slope at t={climbed:F2}");

        float drop = frames[0].pos.y - Last.pos.y;
        if (drop < 2f) Fail($"didn't make it down the slope (dropped {drop:F2}m)");

        List<Frame> onRamp = frames.Where(f => f.pos.z > 0.5f && f.pos.y > 0.4f).ToList();
        int hops = 0;
        for (int i = 1; i < onRamp.Count; i++) if (onRamp[i].pos.y > onRamp[i - 1].pos.y + 0.005f) hops++;
        if (hops > 2) Fail($"bouncing going down the slope ({hops} upward steps)");
        int groundedToggles = Toggles(onRamp, f => f.grounded);
        if (groundedToggles > 2) Fail($"grounded state flickered {groundedToggles} times going down the slope");
        if (onRamp.Count > 0)
        {
            float inInk = onRamp.Count(f => f.inInk) / (float)onRamp.Count;
            if (inInk < 0.95f) Fail($"only in ink {inInk:P0} of the time on a fully inked slope");
        }
    }

    IEnumerator SteepRamp()
    {
        float topY = RampSurfacePoint(60f, Ramp60Length, Ramp60Length).y;
        yield return HoldUntil(Fwd, true, 3f, "up", f => f.pos.y > topY - 1f);
        if (FirstTime(frames, f => f.climbing) < 0f) Fail("never entered climb mode on a 60° slope");
        float rise = frames.Max(f => f.pos.y) - frames[0].pos.y;
        if (rise < 2f) Fail($"didn't make progress up the steep slope (rose {rise:F2}m)");
    }

    // Team of the ink directly below a world point (0 if none).
    static int TeamAt(Vector3 point)
    {
        if (Physics.Raycast(point + Vector3.up, Vector3.down, out RaycastHit hit, 2f, PhysicsLayers.Environment, QueryTriggerInteraction.Ignore)
            && hit.collider.TryGetComponent(out SurfaceInkManager ink))
            return ink.getSurfaceTeam(ink.UVFromHit(hit));
        return 0;
    }

    // Plays the other client: a fake remote enemy joins, its shots and hits arrive as messages,
    // and our outgoing messages are captured instead of going to Steam.
    IEnumerator Combat()
    {
        NetGameManager gm = NetGameManager.Instance;
        const ulong RemoteId = 1001;
        int enemyTeam = player.Team == 1 ? 2 : 1;
        var sent = new List<(NetMsg id, object data, ulong target)>();
        NetGameManager.SendOverride = (id, data, target) => sent.Add((id, data, target));

        Vector3 remotePos = station.TransformPoint(new Vector3(6f, 0.05f, -6f));
        gm.Receive(NetMsg.PlayerSpawn, new PlayerSpawnData { steamId = RemoteId, team = enemyTeam, position = remotePos, playerName = "TestRemote" }, RemoteId);
        yield return Hold(Still, false, 0.1f, "join");
        gm.Receive(NetMsg.PlayerState, new PlayerStateData { steamId = RemoteId, position = remotePos, moveDir = Vector2.zero, team = enemyTeam }, RemoteId);
        yield return Hold(Still, false, 0.2f, "join");

        PlayerController remote = gm.GetPlayer(RemoteId);
        if (remote == null || remote.Hitbox == null || player.Hitbox == null)
        {
            Fail("fake remote player (or a hitbox) didn't spawn");
            NetGameManager.SendOverride = null;
            yield break;
        }
        float fullHealth = player.Hitbox.Health;

        // 1. Replayed enemy shots (one onto us, one onto the floor) are visual only.
        Vector3 floorSpot = station.TransformPoint(new Vector3(-8f, 0f, -6f));
        int floorTeam = TeamAt(floorSpot);
        int activeBefore = ProjectileManager.LiveCount;
        gm.Receive(NetMsg.ProjectileSpawn, new ProjectileSpawnData
        {
            shooterSteamId = RemoteId, team = enemyTeam, origin = player.BodyCenter + Vector3.up * 3f,
            velocities = new float[] { 0f, -8f, 0f }, splashSizes = new[] { 10 }, visible = new[] { true }
        }, RemoteId);
        gm.Receive(NetMsg.ProjectileSpawn, new ProjectileSpawnData
        {
            shooterSteamId = RemoteId, team = enemyTeam, origin = floorSpot + Vector3.up * 3f,
            velocities = new float[] { 0f, -8f, 0f }, splashSizes = new[] { 10 }, visible = new[] { false }
        }, RemoteId);
        yield return Hold(Still, false, 1f / 30f, "replay");
        int replays = ProjectileManager.LiveCount - activeBefore;
        yield return Hold(Still, false, 0.8f, "replay");

        if (replays != 2) Fail($"expected 2 replayed projectiles, saw {replays}");
        if (player.Hitbox.Health < fullHealth) Fail($"a replayed shot damaged us ({player.Hitbox.Health}/{fullHealth})");
        if (TeamAt(floorSpot) != floorTeam) Fail("a replayed shot painted the floor (only the shooter's Splat message should)");
        if (sent.Any(m => m.id == NetMsg.Damage || m.id == NetMsg.Splat)) Fail("replayed shots sent Damage/Splat messages");

        // 2. Our shots hitting the remote copy are forwarded to that player, not applied locally.
        sent.Clear();
        for (int i = 0; i < 3; i++)
        {
            ProjectileManager.Fire(projectilePrefab, remote.BodyCenter + Vector3.up * 3f, Vector3.down * 8f, 10, player.Team, true, authoritative: true, ownerId: player.OwnerId);
            yield return Hold(Still, false, 0.25f, "shoot");
        }
        yield return Hold(Still, false, 0.5f, "shoot");

        var hits = sent.Where(m => m.id == NetMsg.Damage).Select(m => (data: (DamageData)m.data, m.target)).ToList();
        Note($"{hits.Count} Damage messages for 3 hits");
        if (hits.Count != 3) Fail($"expected 3 Damage messages for 3 hits, got {hits.Count}");
        foreach (var (d, target) in hits)
        {
            if (target != RemoteId || d.targetSteamId != RemoteId) { Fail($"Damage sent to {target}/{d.targetSteamId}, not the victim {RemoteId}"); break; }
            if (d.attackerSteamId != player.OwnerId || d.fromTeam != player.Team || d.amount <= 0f) { Fail($"bad Damage message (attacker {d.attackerSteamId}, team {d.fromTeam}, amount {d.amount})"); break; }
        }
        if (remote.Hitbox.Health < fullHealth) Fail("the shooter changed the remote copy's health locally");

        // 3. Incoming Damage: friendly or misaddressed hits are ignored; enemy hits kill us.
        float amount = hits.Count > 0 ? hits[0].data.amount : 30f;
        gm.Receive(NetMsg.Damage, new DamageData { targetSteamId = player.OwnerId, attackerSteamId = RemoteId, amount = amount, fromTeam = player.Team }, RemoteId);
        gm.Receive(NetMsg.Damage, new DamageData { targetSteamId = 4242, attackerSteamId = RemoteId, amount = amount, fromTeam = enemyTeam }, RemoteId);
        yield return Hold(Still, false, 0.1f, "hit");
        if (player.Hitbox.Health < fullHealth) Fail("friendly or misaddressed Damage was applied");

        // Healing: none for 0.5s after a hit, then full in 6s (16.7/s), or 1.5s (66.7/s) submerged.
        for (int i = 0; i < 2; i++)
            gm.Receive(NetMsg.Damage, new DamageData { targetSteamId = player.OwnerId, attackerSteamId = RemoteId, amount = amount, fromTeam = enemyTeam }, RemoteId);
        yield return Hold(Still, false, 0.05f, "heal");
        float hurt = player.Hitbox.Health;
        yield return Hold(Still, false, 0.4f, "heal");
        if (player.Hitbox.Health > hurt + 0.01f) Fail($"healed during the cooldown ({hurt:F1} -> {player.Hitbox.Health:F1})");

        yield return Hold(Still, false, 0.2f, "heal");
        float h0 = player.Hitbox.Health;
        yield return Hold(Still, false, 0.5f, "heal");
        float standRate = (player.Hitbox.Health - h0) / 0.5f;

        yield return Hold(Still, true, 0.3f, "heal"); // into squid form on own ink
        bool submerged = player.IsInInk;
        h0 = player.Hitbox.Health;
        yield return Hold(Still, true, 0.25f, "heal");
        float submergedRate = (player.Hitbox.Health - h0) / 0.25f;
        Note($"heal {standRate:F1}/s standing, {submergedRate:F1}/s submerged");
        if (Mathf.Abs(standRate - 100f / 6f) > 1.5f) Fail($"standing heal rate {standRate:F1}/s, expected ~16.7");
        if (!submerged) Fail("not submerged in own ink for the submerged heal check");
        else if (Mathf.Abs(submergedRate - 100f / 1.5f) > 4f) Fail($"submerged heal rate {submergedRate:F1}/s, expected ~66.7");

        yield return Hold(Still, true, 1.5f, "heal");
        if (Mathf.Abs(player.Hitbox.Health - fullHealth) > 0.01f) Fail($"didn't heal to exactly full ({player.Hitbox.Health:F2}/{fullHealth})");
        yield return Hold(Still, false, 0.3f, "heal"); // back to standing

        int hitsToKill = Mathf.CeilToInt(fullHealth / amount);
        for (int i = 0; i < hitsToKill; i++)
        {
            gm.Receive(NetMsg.Damage, new DamageData { targetSteamId = player.OwnerId, attackerSteamId = RemoteId, amount = amount, fromTeam = enemyTeam }, RemoteId);
            yield return Hold(Still, false, 0.05f, "hit");
            float expected = fullHealth - amount * (i + 1);
            if (i < hitsToKill - 1 && Mathf.Abs(player.Hitbox.Health - expected) > 0.01f)
            {
                Fail($"health {player.Hitbox.Health} after {i + 1} hits, expected {expected}");
                break;
            }
        }
        if (!player.IsDead) Fail($"not dead after {hitsToKill} hits of {amount}");
        else Note($"died after {hitsToKill} hits");

        // Death respawns after 5s, at full health.
        yield return HoldUntil(Still, false, 7f, "dead", f => !f.dead);
        if (player.IsDead) Fail("didn't respawn within 7s");
        else if (player.Hitbox.Health < fullHealth) Fail($"respawned with {player.Hitbox.Health}/{fullHealth} health");

        // 4. The remote leaving removes its whole entity.
        gm.Receive(NetMsg.PlayerDespawn, new PlayerSpawnData { steamId = RemoteId }, RemoteId);
        yield return Hold(Still, false, 0.1f, "leave");
        if (gm.GetPlayer(RemoteId) != null || GameObject.Find("PlayerEntity - TestRemote") != null)
            Fail("the remote's entity (or its root) is still in the scene after it left");

        NetGameManager.SendOverride = null;
    }

    // Drives the host menu through its real buttons.
    IEnumerator HostMenuTeams()
    {
        HostMenu menu = FindFirstObjectByType<HostMenu>();
        MatchHUD hud = FindFirstObjectByType<MatchHUD>();
        if (menu == null || hud == null) { Fail("no HostMenu/MatchHUD in the scene"); yield break; }
        NetGameManager gm = NetGameManager.Instance;
        const ulong RemoteId = 3003;
        var sent = new List<(NetMsg id, object data, ulong target)>();
        NetGameManager.SendOverride = (id, data, target) => sent.Add((id, data, target));

        if (hud.ShowPercentages) Fail("coverage percentages are shown by default");

        Vector3 remotePos = station.TransformPoint(new Vector3(6f, 0.05f, 6f));
        gm.Receive(NetMsg.PlayerSpawn, new PlayerSpawnData { steamId = RemoteId, team = 2, position = remotePos, playerName = "Benchwarmer" }, RemoteId);
        yield return Hold(Still, false, 0.1f, "menu");
        gm.Receive(NetMsg.PlayerState, new PlayerStateData { steamId = RemoteId, position = remotePos, moveDir = Vector2.zero, team = 2 }, RemoteId);
        yield return Hold(Still, false, 0.1f, "menu");
        PlayerController remote = gm.GetPlayer(RemoteId);

        menu.Toggle();
        if (!menu.IsOpen || !InputGate.Blocked) Fail("menu didn't open (or didn't block gameplay input)");

        menu.PercentButton.onClick.Invoke();
        if (!hud.ShowPercentages) Fail("Toggle percentages didn't show them");
        menu.PercentButton.onClick.Invoke();
        if (hud.ShowPercentages) Fail("Toggle percentages didn't hide them again");
        PlayerLoadout loadout = player.GetComponent<PlayerLoadout>();
        loadout.SetSpecialPoints(0f);
        menu.FillSpecialButton.onClick.Invoke();
        if (!loadout.SpecialReady) Fail("Fill special didn't fill it");
        if (menu.IsOpen) Fail("menu stayed open after filling the special");
        loadout.SetSpecialPoints(0f);
        menu.SetOpen(true); // carry on with the rest of the menu

        menu.TeamsButton.onClick.Invoke();
        yield return Hold(Still, false, 0.05f, "menu");
        HostMenu.SlotButton[][] cols = menu.Columns; // Alpha, Beta, Spectate, Not playing
        if (!menu.TeamEditorOpen) { Fail("team editor didn't open"); NetGameManager.SendOverride = null; yield break; }
        if (!cols[0][0].label.text.StartsWith(player.DisplayName) || cols[1][0].label.text != "Benchwarmer")
            Fail($"editor layout wrong: alpha0 \"{cols[0][0].label.text}\", beta0 \"{cols[1][0].label.text}\"");

        void Move(int fromCol, int fromSlot, int toCol, int toSlot)
        {
            cols[fromCol][fromSlot].button.onClick.Invoke();
            cols[toCol][toSlot].button.onClick.Invoke();
        }

        // Enemy to Spectate: hidden, off the HUD, roster broadcast.
        Move(1, 0, 2, 0);
        yield return Hold(Still, false, 0.1f, "menu");
        if (remote.Role != PlayerRole.Spectator || remote.transform.root.gameObject.activeSelf) Fail("spectator wasn't benched/hidden");
        if (hud.BetaSlots[0].playerName.text != "") Fail("spectator still shown on the HUD");
        bool broadcast = sent.Any(m => m.id == NetMsg.TeamAssign && m.data is TeamAssignData d
                                       && System.Array.IndexOf(d.ids, RemoteId) is int k && k >= 0 && d.roles[k] == (int)PlayerRole.Spectator);
        if (!broadcast) Fail("no TeamAssign broadcast for the change");

        // Us to Beta: team changes, HUD follows, we respawn.
        Move(0, 0, 1, 0);
        yield return Hold(Still, false, 0.2f, "menu");
        if (player.Team != 2 || hud.BetaSlots[0].playerName.text != player.DisplayName) Fail($"moving us to Beta failed (team {player.Team})");

        // Us to Not playing, then back to Alpha.
        Move(1, 0, 3, 0);
        yield return Hold(Still, false, 0.1f, "menu");
        if (player.transform.root.gameObject.activeSelf || player.Role != PlayerRole.NotPlaying) Fail("benching ourselves didn't hide our player");
        Move(3, 0, 0, 0);
        yield return Hold(Still, false, 0.3f, "menu");
        if (!player.transform.root.gameObject.activeSelf || player.Team != 1) Fail("returning to Alpha didn't restore our player");

        menu.CloseButton.onClick.Invoke();
        if (menu.IsOpen || InputGate.Blocked) Fail("menu didn't close (or left input blocked)");

        gm.Receive(NetMsg.PlayerDespawn, new PlayerSpawnData { steamId = RemoteId }, RemoteId);
        yield return Hold(Still, false, 0.1f, "menu");
        NetGameManager.SendOverride = null;
    }

    IEnumerator MapTeam()
    {
        MapScreen map = FindFirstObjectByType<MapScreen>();
        if (map == null) { Fail("no MapScreen in the scene"); yield break; }
        NetGameManager gm = NetGameManager.Instance;
        const ulong MateId = 3003, FoeId = 3004;
        int enemyTeam = player.Team == 1 ? 2 : 1;
        NetGameManager.SendOverride = (id, data, target) => { };
        Vector3 matePos = station.TransformPoint(new Vector3(8f, 0.05f, 6f));
        Vector3 foePos = station.TransformPoint(new Vector3(-8f, 0.05f, 6f));
        gm.Receive(NetMsg.PlayerSpawn, new PlayerSpawnData { steamId = MateId, team = player.Team, position = matePos, playerName = "Mate" }, MateId);
        gm.Receive(NetMsg.PlayerState, new PlayerStateData { steamId = MateId, position = matePos, moveDir = Vector2.zero, team = player.Team }, MateId);
        gm.Receive(NetMsg.PlayerSpawn, new PlayerSpawnData { steamId = FoeId, team = enemyTeam, position = foePos, playerName = "Foe" }, FoeId);
        gm.Receive(NetMsg.PlayerState, new PlayerStateData { steamId = FoeId, position = foePos, moveDir = Vector2.zero, team = enemyTeam }, FoeId);
        ISuperJumpTarget picked = null;
        Action<ISuperJumpTarget> onPick = t => picked = t;
        MapScreen.SuperJumpRequested += onPick;

        map.ScriptedHold = true;
        yield return Hold(Still, false, 0.3f, "map");
        if (!map.IsOpen || !InputGate.Blocked) Fail("holding didn't open the map (or didn't block gameplay input)");
        if (map.MapTexture == null || !map.MapTexture.IsCreated()) Fail("map wasn't rendered");

        // Our team only, in a stable order, empty slots padded out.
        MapScreen.Entry[] entries = map.Entries;
        int self = Array.FindIndex(entries, e => e.playerName.text == player.DisplayName);
        int mate = Array.FindIndex(entries, e => e.playerName.text == "Mate");
        Note($"entries: {string.Join(", ", entries.Select(e => $"{e.playerName.text}/{e.status.text}"))}");
        if (self < 0 || mate < 0) { Fail("our team isn't listed"); map.ScriptedHold = false; MapScreen.SuperJumpRequested -= onPick; yield break; }
        if (entries.Any(e => e.playerName.text == "Foe")) Fail("an enemy is listed");
        if (entries.Count(e => !e.marker.gameObject.activeSelf && !e.line.gameObject.activeSelf) != 2) Fail("empty slots should have no marker or line");

        // Angled view of the map layers only; markers sit where the players appear on it.
        Camera cam = map.MapCamera;
        float tilt = Vector3.Angle(cam.transform.forward, Vector3.down);
        Note($"map camera tilt {tilt:F0}° from straight down, yaw {cam.transform.eulerAngles.y:F0}°");
        if (tilt < 5f || tilt > 45f) Fail("map view isn't a slight angle");
        if (cam.cullingMask != LayerMask.GetMask("Map", "InkSurface")) Fail("map draws more than the map layers");
        PlayerController mateCtrl = gm.Players.First(pc => pc != null && pc.OwnerId == MateId);
        Vector2 markerPos = ((RectTransform)entries[mate].marker.transform).anchoredPosition;
        Vector2 expected = map.MapPosition(mateCtrl.transform.position);
        if (Vector2.Distance(markerPos, expected) > 1f) Fail($"teammate's marker at {markerPos}, expected {expected}");

        // Each line runs from its entry to its marker.
        foreach (int i in new[] { self, mate })
        {
            MapScreen.Entry e = entries[i];
            Vector3 mid = (e.lineStart.position + e.marker.transform.position) * 0.5f;
            float length = Vector3.Distance(e.lineStart.position, e.marker.transform.position) / e.line.lossyScale.x;
            if (Vector3.Distance(e.line.position, mid) > 2f || Mathf.Abs(e.line.rect.width - length) > 2f)
                Fail($"line {i} doesn't connect its entry to its marker");
        }

        // We can't jump to ourselves; picking the teammate's marker requests it and closes the map.
        if (entries[self].button.interactable || entries[self].marker.interactable) Fail("own entry is clickable");
        if (!entries[mate].button.interactable) Fail("teammate's entry isn't clickable");
        entries[self].button.onClick.Invoke();
        if (picked != null || !map.IsOpen) Fail("picking ourselves did something");
        entries[mate].marker.onClick.Invoke();
        yield return Hold(Still, false, 0.2f, "map");
        if (!(picked is PlayerController pickedPlayer) || pickedPlayer.DisplayName != "Mate") Fail("picking the teammate didn't request a Super Jump to them");
        if (!player.IsSuperJumping) Fail("picking the teammate didn't start a Super Jump");
        if (map.IsOpen || InputGate.Blocked) Fail("map didn't close after picking (or reopened while still held)");
        player.CancelSuperJump(); // the jump itself is covered by the SuperJump scenario

        // Release and hold again: a splatted teammate can't be picked.
        map.ScriptedHold = false;
        yield return Hold(Still, false, 0.1f, "map");
        gm.Receive(NetMsg.PlayerState, new PlayerStateData { steamId = MateId, position = matePos, moveDir = Vector2.zero, team = player.Team, dead = true }, MateId);
        map.ScriptedHold = true;
        yield return Hold(Still, false, 0.2f, "map");
        if (!map.IsOpen) Fail("map didn't reopen after releasing");
        if (entries[mate].button.interactable || entries[mate].status.text != "Splatted") Fail("splatted teammate is still pickable");
        map.ScriptedHold = false;
        yield return Hold(Still, false, 0.1f, "map");
        if (map.IsOpen || InputGate.Blocked) Fail("releasing didn't close the map");

        MapScreen.SuperJumpRequested -= onPick;
        gm.Receive(NetMsg.PlayerDespawn, new PlayerSpawnData { steamId = MateId }, MateId);
        gm.Receive(NetMsg.PlayerDespawn, new PlayerSpawnData { steamId = FoeId }, FoeId);
        yield return Hold(Still, false, 0.1f, "map");
        NetGameManager.SendOverride = null;
    }

    IEnumerator SuperJump()
    {
        NetGameManager gm = NetGameManager.Instance;
        const ulong MateId = 3005;
        NetGameManager.SendOverride = (id, data, target) => { };
        Vector3 matePos = station.TransformPoint(new Vector3(9f, 0.05f, 7f));
        gm.Receive(NetMsg.PlayerSpawn, new PlayerSpawnData { steamId = MateId, team = player.Team, position = matePos, playerName = "Mate" }, MateId);
        gm.Receive(NetMsg.PlayerState, new PlayerStateData { steamId = MateId, position = matePos, moveDir = Vector2.zero, team = player.Team }, MateId);
        yield return Hold(Still, false, 0.3f, "settle");
        PlayerController mate = gm.Players.FirstOrDefault(pc => pc != null && pc.OwnerId == MateId);
        if (mate == null) { Fail("teammate didn't spawn"); NetGameManager.SendOverride = null; yield break; }
        float Flat(Vector3 v) => new Vector2(v.x, v.z).magnitude;

        // First landing without swim held, then (after the teammate moves) with it held.
        for (int run = 0; run < 2; run++)
        {
            bool holdSwim = run == 1;
            if (!player.StartSuperJump(mate)) { Fail("couldn't start a Super Jump to a teammate"); break; }
            if (player.StartSuperJump(mate)) Fail("a second jump started during the first");

            // Charge: forced into swim form and held in place, even pushing forward with swim released.
            string charge = "charge" + run, fly = "fly" + run;
            yield return HoldUntil(Fwd, false, 3f, charge, f => player.SuperJumpState != SuperJumpPhase.Charging);
            List<Frame> charging = frames.Where(f => f.phase == charge).ToList();
            float drift = charging.Max(f => Flat(f.pos - charging[0].pos));
            Note($"run {run}: charged {charging.Count * Dt:F2}s, drifted {drift:F3}m");
            if (Mathf.Abs(charging.Count * Dt - player.SuperJumpChargeTime) > 0.1f) Fail($"charge took {charging.Count * Dt:F2}s, not {player.SuperJumpChargeTime:F2}s");
            if (charging.Skip(1).Any(f => !f.squid)) Fail("not forced into swim form while charging");
            if (drift > 0.05f) Fail("moved while charging");
            if (player.SuperJumpState != SuperJumpPhase.Flying) { Fail("didn't launch after charging"); break; }

            // Flight: a high arc, staying a squid with its nose along the path.
            yield return HoldUntil(Still, holdSwim, 5f, fly, f => !player.IsSuperJumping);
            List<Frame> flying = frames.Where(f => f.phase == fly).ToList();
            float peak = flying.Max(f => f.pos.y) - charging.Last().pos.y;
            float alignSum = 0f; int alignCount = 0;
            for (int i = flying.Count / 8; i < flying.Count - flying.Count / 8 - 1; i++)
            {
                Vector3 travel = station.TransformDirection(flying[i + 1].pos - flying[i - 1].pos).normalized;
                alignSum += Vector3.Dot(travel, flying[i].squidRot * Vector3.forward);
                alignCount++;
            }
            float align = alignCount > 0 ? alignSum / alignCount : 0f;
            Note($"run {run}: flew {flying.Count * Dt:F2}s, peak {peak:F1}m up, squid/path alignment {align:F3}");
            if (peak < 10f) Fail($"arc only peaked {peak:F1}m up");
            if (flying.Any(f => !f.squid)) Fail("left swim form mid-flight");
            if (align < 0.9f) Fail("squid doesn't point along its flight path"); // eased, so it lags where a tall arc turns sharply

            // Landed on the teammate and unlocked; swim form kept only if held.
            yield return Hold(Still, holdSwim, 0.4f, "land" + run);
            float miss = Flat(player.transform.position - mate.transform.position);
            Note($"run {run}: landed {miss:F2}m from the teammate");
            if (miss > 1.5f) Fail($"landed {miss:F2}m from the teammate");
            if (player.IsSuperJumping) Fail("still Super Jumping after landing");
            if (player.IsSquid != holdSwim) Fail(holdSwim ? "dropped out of swim form while it was held" : "stayed in swim form without swim held");
            Vector3 before = player.transform.position;
            yield return Hold(Fwd, holdSwim, 0.6f, "after" + run);
            if (Flat(player.transform.position - before) < 0.3f) Fail("can't move after landing");

            // Move the teammate for the second run.
            Vector3 next = station.TransformPoint(new Vector3(-9f, 0.05f, 7f));
            gm.Receive(NetMsg.PlayerState, new PlayerStateData { steamId = MateId, position = next, moveDir = Vector2.zero, team = player.Team }, MateId);
            yield return Hold(Still, false, 0.6f, "settle");
        }

        gm.Receive(NetMsg.PlayerDespawn, new PlayerSpawnData { steamId = MateId }, MateId);
        yield return Hold(Still, false, 0.1f, "settle");
        NetGameManager.SendOverride = null;
    }

    IEnumerator SuperJumpLocking()
    {
        NetGameManager gm = NetGameManager.Instance;
        MapScreen map = FindFirstObjectByType<MapScreen>();
        const ulong MateId = 3006;
        NetGameManager.SendOverride = (id, data, target) => { };
        float Flat(Vector3 v) => new Vector2(v.x, v.z).magnitude;
        Vector3 clickedAt = station.TransformPoint(new Vector3(8f, 0.05f, 6f));
        SpawnRemote(MateId, player.Team, clickedAt, "Mate");
        yield return Hold(Still, false, 0.3f, "settle");
        PlayerController mate = gm.GetPlayer(MateId);
        if (mate == null) { Fail("teammate didn't spawn"); NetGameManager.SendOverride = null; yield break; }

        // The teammate moves away and then leaves mid-charge: we still land where they were.
        player.StartSuperJump(mate);
        gm.Receive(NetMsg.PlayerState, new PlayerStateData { steamId = MateId, position = station.TransformPoint(new Vector3(-8f, 0.05f, 6f)), moveDir = Vector2.zero, team = player.Team }, MateId);
        yield return Hold(Still, false, 0.5f, "charge");
        Despawn(MateId);
        yield return HoldUntil(Still, false, 6f, "jump", f => !player.IsSuperJumping);
        yield return Hold(Still, false, 0.3f, "land");
        float miss = Flat(player.transform.position - clickedAt);
        Note($"landed {miss:F2}m from where the teammate was when picked");
        if (miss > 1.5f) Fail($"landed {miss:F2}m from the locked spot (followed the teammate, or cancelled)");

        // Dying mid-charge cancels.
        player.StartSuperJump(new SpawnJumpTarget(player.Team, player.SpawnPosition));
        yield return Hold(Still, false, 0.3f, "charge");
        player.OnDeath();
        yield return Hold(Still, false, 0.1f, "dead");
        if (player.IsSuperJumping) Fail("still Super Jumping after dying");
        player.Respawn();
        yield return Hold(Still, false, 0.3f, "settle");

        // The spawn entry on the map is always there; picking it jumps home.
        player.PlaceAt(player.transform.position + station.TransformDirection(new Vector3(9f, 0f, 7f)), player.transform.eulerAngles.y);
        frames.Clear(); // the teleport isn't a movement glitch
        map.ScriptedHold = true;
        yield return Hold(Still, false, 0.3f, "map");
        if (!map.SpawnEntry.button.interactable || !map.SpawnEntry.marker.gameObject.activeInHierarchy) Fail("spawn entry isn't available");
        map.SpawnEntry.button.onClick.Invoke();
        map.ScriptedHold = false;
        if (!player.IsSuperJumping) Fail("picking the spawn didn't start a Super Jump");
        else
        {
            yield return HoldUntil(Still, false, 6f, "jump", f => !player.IsSuperJumping);
            yield return Hold(Still, false, 0.3f, "land");
            float home = Flat(player.transform.position - player.SpawnPosition);
            Note($"landed {home:F2}m from the spawn");
            if (home > 1.5f) Fail($"landed {home:F2}m from the spawn");
        }
        NetGameManager.SendOverride = null;
    }

    IEnumerator SpecialDeathAndSync()
    {
        NetGameManager gm = NetGameManager.Instance;
        PlayerLoadout loadout = player.GetComponent<PlayerLoadout>();
        MatchHUD hud = FindFirstObjectByType<MatchHUD>();
        if (loadout == null || hud == null) { Fail("no PlayerLoadout or MatchHUD"); yield break; }
        NetGameManager.SendOverride = (id, data, target) => { };
        const ulong FoeId = 3014;
        int enemyTeam = player.Team == 1 ? 2 : 1;
        MatchHUD.PlayerSlot[] own = player.Team == 1 ? hud.AlphaSlots : hud.BetaSlots;
        MatchHUD.PlayerSlot[] enemy = player.Team == 1 ? hud.BetaSlots : hud.AlphaSlots;

        // Ours: in the network state and on our slot when charged.
        loadout.SetSpecial(SpecialType.BubbleShield);
        loadout.SetSpecialPoints(1000f);
        yield return Hold(Still, false, 0.1f, "sync");
        if (!player.GetNetState(0, 0).specialReady) Fail("charged special isn't in the network state");
        if (!own[0].specialMark.gameObject.activeSelf) Fail("our slot doesn't show the charged special");

        // Splatted: half of it is lost.
        player.OnDeath();
        yield return Hold(Still, false, 0.1f, "dead");
        Note($"special {loadout.SpecialPoints:F0}p after dying at 1000p");
        if (Mathf.Abs(loadout.SpecialPoints - 500f) > 0.01f) Fail("dying didn't halve the special");
        if (own[0].specialMark.gameObject.activeSelf) Fail("slot still shows a special after losing it");
        player.Respawn();
        yield return Hold(Still, false, 0.2f, "settle");

        // Theirs: from their state updates.
        Vector3 foePos = station.TransformPoint(new Vector3(-9f, 0.05f, 8f));
        SpawnRemote(FoeId, enemyTeam, foePos, "Foe");
        yield return Hold(Still, false, 0.1f, "sync");
        if (enemy[0].specialMark.gameObject.activeSelf) Fail("enemy slot shows a special before they have one");
        gm.Receive(NetMsg.PlayerState, new PlayerStateData { steamId = FoeId, position = foePos, moveDir = Vector2.zero, team = enemyTeam, specialReady = true }, FoeId);
        yield return Hold(Still, false, 0.1f, "sync");
        if (!enemy[0].specialMark.gameObject.activeSelf) Fail("enemy's charged special isn't shown");

        loadout.SetSpecialPoints(0f);
        Despawn(FoeId);
        yield return Hold(Still, false, 0.1f, "sync");
        NetGameManager.SendOverride = null;
    }

    // ── Subs and specials ───────────────────────────────────────────────────

    readonly List<(NetMsg id, object data, ulong to)> sent = new List<(NetMsg, object, ulong)>();

    void CaptureSends()
    {
        sent.Clear();
        NetGameManager.SendOverride = (id, data, to) => sent.Add((id, data, to));
    }

    // Spawns on the next frame (messages are handled on the main-thread queue).
    void SpawnRemote(ulong id, int team, Vector3 pos, string name)
    {
        NetGameManager gm = NetGameManager.Instance;
        gm.Receive(NetMsg.PlayerSpawn, new PlayerSpawnData { steamId = id, team = team, position = pos, playerName = name }, id);
        gm.Receive(NetMsg.PlayerState, new PlayerStateData { steamId = id, position = pos, moveDir = Vector2.zero, team = team }, id);
    }

    void Despawn(ulong id) => NetGameManager.Instance.Receive(NetMsg.PlayerDespawn, new PlayerSpawnData { steamId = id }, id);

    void Shoot(Vector3 above, int team, ulong ownerId) =>
        ProjectileManager.Fire(projectilePrefab, above, Vector3.down * 10f, 10, team, true, authoritative: true, ownerId: ownerId);

    IEnumerator SubBeacon()
    {
        NetGameManager gm = NetGameManager.Instance;
        PlayerLoadout loadout = player.GetComponent<PlayerLoadout>();
        MapScreen map = FindFirstObjectByType<MapScreen>();
        if (loadout == null || map == null) { Fail("player has no PlayerLoadout (or no MapScreen)"); yield break; }
        if (loadout.SubReadyLight == null) Fail("the ink tank's sub light isn't hooked up");
        loadout.SetSub(SubType.Beacon);
        if (Mathf.Abs(loadout.SubInkCost - loadout.BeaconPrefab.InkCost) > 0.001f) Fail("the sub's cost doesn't come from its prefab");
        CaptureSends();
        SubDevice.RemoveAll();
        const ulong FoeId = 3011;
        int enemyTeam = player.Team == 1 ? 2 : 1;

        // Costs the sub's share of ink; with less than that, nothing happens.
        player.RefillInk();
        yield return Hold(Still, false, 0.2f, "stand");
        PlayerStateData full = player.GetNetState(player.OwnerId, 0);
        bool litFull = loadout.SubReadyLight != null && loadout.SubReadyLight.enabled;
        if (!loadout.UseSub()) { Fail("couldn't place a beacon with a full tank"); NetGameManager.SendOverride = null; yield break; }
        Beacon beacon = SubDevice.All.OfType<Beacon>().First(b => b.OwnerId == player.OwnerId);
        Vector3 beaconPos = beacon.transform.position;
        float ahead = Vector3.Dot(beaconPos - player.transform.position, player.transform.forward);
        Note($"ink after placing {player.InkLevel:F2}, beacon {ahead:F1}m ahead");
        if (Mathf.Abs(player.InkLevel - 0.3f) > 0.01f) Fail($"ink went to {player.InkLevel:F2}, not 0.30");
        if (ahead < 0.5f) Fail("beacon isn't in front of the player");
        if (!sent.Any(m => m.id == NetMsg.SubSpawn)) Fail("beacon wasn't broadcast");
        if (loadout.UseSub() || SubDevice.All.Count != 1) Fail("placed a beacon with only 30% ink");
        yield return null;
        PlayerStateData low = player.GetNetState(player.OwnerId, 0);
        bool litLow = loadout.SubReadyLight != null && loadout.SubReadyLight.enabled;
        Note($"tank light {(litFull ? "on" : "off")} when full, {(litLow ? "on" : "off")} at {player.InkLevel:F2}; sent ink {full.ink:F2}/{low.ink:F2}, ready {full.subReady}/{low.subReady}");
        if (!litFull || litLow) Fail("the tank light should be on only with enough ink for the sub");
        if (!full.subReady || low.subReady || Mathf.Abs(low.ink - player.InkLevel) > 0.01f) Fail("ink level and sub readiness aren't in our state updates");

        // An enemy beacon arrives from its owner; only ours shows on our map.
        SpawnRemote(FoeId, enemyTeam, station.TransformPoint(new Vector3(-9f, 0.05f, 8f)), "Foe");
        yield return Hold(Still, false, 0.1f, "stand");

        // Their tank shows their ink, and its light whether they can use their sub.
        PlayerController foe = gm.GetPlayer(FoeId);
        PlayerLoadout foeLoadout = foe != null ? foe.GetComponent<PlayerLoadout>() : null;
        foreach (bool ready in new[] { true, false })
        {
            PlayerStateData state = foe.GetNetState(FoeId, 0);
            state.subReady = ready;
            state.ink = ready ? 0.8f : 0.2f;
            foe.ApplyNetState(state);
            yield return null;
            if (foeLoadout == null || foeLoadout.SubReadyLight == null || foeLoadout.SubReadyLight.enabled != ready) Fail($"their tank light didn't follow their state (ready {ready})");
            if (Mathf.Abs(foe.InkLevel - state.ink) > 0.001f) Fail("their ink level didn't follow their state");
        }

        gm.Receive(NetMsg.SubSpawn, new SubData { ownerId = FoeId, subId = 1, team = enemyTeam, subType = (int)SubType.Beacon, landed = true, normal = Vector3.up, position = station.TransformPoint(new Vector3(-8f, 0f, 8f)) }, FoeId);
        yield return Hold(Still, false, 0.1f, "stand");
        if (SubDevice.Find(FoeId, 1) == null) Fail("enemy beacon wasn't created from its message");

        // Walk away, open the map and pick our beacon.
        player.PlaceAt(player.transform.position + station.TransformDirection(new Vector3(9f, 0f, -7f)), player.transform.eulerAngles.y);
        frames.Clear(); // the teleport isn't a movement glitch
        map.ScriptedHold = true;
        yield return Hold(Still, false, 0.3f, "map");
        if (map.ShownBeacons.Count != 1 || map.ShownBeacons[0] != beacon) Fail($"map shows {map.ShownBeacons.Count} beacon(s), expected just ours");
        else if (!map.BeaconMarkers[0].gameObject.activeSelf) Fail("our beacon has no marker");
        else map.BeaconMarkers[0].onClick.Invoke();
        map.ScriptedHold = false;
        if (!player.IsSuperJumping) { Fail("picking the beacon didn't start a Super Jump"); }
        else
        {
            yield return HoldUntil(Still, false, 6f, "jump", f => !player.IsSuperJumping);
            yield return Hold(Still, false, 0.3f, "land");
            float miss = new Vector2(player.transform.position.x - beaconPos.x, player.transform.position.z - beaconPos.z).magnitude;
            Note($"landed {miss:F2}m from the beacon");
            if (miss > 1.5f) Fail($"landed {miss:F2}m from the beacon");
            if (beacon != null) Fail("landing on the beacon didn't break it");
            if (!sent.Any(m => m.id == NetMsg.SubDestroy)) Fail("the break wasn't broadcast");
        }

        Despawn(FoeId);
        yield return Hold(Still, false, 0.1f, "stand");
        if (SubDevice.Find(FoeId, 1) != null) Fail("enemy beacon outlived its owner leaving");
        NetGameManager.SendOverride = null;
    }

    IEnumerator BeaconHealth()
    {
        NetGameManager gm = NetGameManager.Instance;
        PlayerLoadout loadout = player.GetComponent<PlayerLoadout>();
        if (loadout == null) { Fail("player has no PlayerLoadout"); yield break; }
        loadout.SetSub(SubType.Beacon);
        CaptureSends();
        SubDevice.RemoveAll();
        const ulong FoeId = 3012;
        int enemyTeam = player.Team == 1 ? 2 : 1;

        player.RefillInk();
        loadout.UseSub();
        Beacon beacon = SubDevice.All.OfType<Beacon>().FirstOrDefault(b => b.OwnerId == player.OwnerId);
        if (beacon == null) { Fail("couldn't place a beacon"); NetGameManager.SendOverride = null; yield break; }

        // An enemy shot takes a chunk; friendly fire doesn't; the next enemy hit breaks it.
        Shoot(beacon.transform.position + Vector3.up * 3f, enemyTeam, 4242);
        yield return Hold(Still, false, 0.6f, "shot");
        float afterShot = beacon != null ? beacon.Health : 0f;
        Note($"health after an enemy shot {afterShot:F0}/35");
        if (beacon == null || afterShot <= 0f || afterShot >= 35f) Fail("an enemy shot didn't damage it (or broke it outright)");
        Shoot(beacon.transform.position + Vector3.up * 3f, player.Team, player.OwnerId);
        yield return Hold(Still, false, 0.6f, "shot");
        if (beacon == null || beacon.Health != afterShot) Fail("our own shot damaged it");
        sent.Clear();
        if (beacon != null) beacon.TakeDamage(afterShot, enemyTeam, 4242);
        yield return null;
        if (beacon != null) Fail("didn't break at 0 health");
        if (!sent.Any(m => m.id == NetMsg.SubDestroy)) Fail("breaking wasn't broadcast");

        // Lifetime: one left alone expires.
        player.RefillInk();
        loadout.UseSub();
        Beacon timed = SubDevice.All.OfType<Beacon>().FirstOrDefault(b => b.OwnerId == player.OwnerId);
        if (timed != null) timed.ExpireIn(0.3f);
        yield return Hold(Still, false, 0.6f, "expire");
        if (timed != null) Fail("didn't expire");

        // Someone else's: our hit goes to its owner (who decides), and charges our special.
        SpawnRemote(FoeId, enemyTeam, station.TransformPoint(new Vector3(-9f, 0.05f, 8f)), "Foe");
        yield return Hold(Still, false, 0.1f, "remote");
        gm.Receive(NetMsg.SubSpawn, new SubData { ownerId = FoeId, subId = 4, team = enemyTeam, subType = (int)SubType.Beacon, landed = true, normal = Vector3.up, position = station.TransformPoint(new Vector3(6f, 0f, 6f)) }, FoeId);
        yield return Hold(Still, false, 0.1f, "remote");
        SubDevice theirs = SubDevice.Find(FoeId, 4);
        if (theirs == null) { Fail("their beacon wasn't created"); }
        else
        {
            sent.Clear();
            float points = loadout.SpecialPoints;
            Shoot(theirs.transform.position + Vector3.up * 3f, player.Team, player.OwnerId);
            yield return Hold(Still, false, 0.6f, "remote");
            var hit = sent.FirstOrDefault(m => m.id == NetMsg.SubDamage);
            Note($"special +{loadout.SpecialPoints - points:F0}p for the hit");
            if (hit.data == null || hit.to != FoeId) Fail("hit on their beacon wasn't sent to its owner");
            if (theirs == null || theirs.Health != 35f) Fail("our copy applied the damage itself");
            if (loadout.SpecialPoints - points < 1f) Fail("hitting their beacon didn't charge the special");
            gm.Receive(NetMsg.SubDestroy, new SubData { ownerId = FoeId, subId = 4 }, FoeId);
            yield return Hold(Still, false, 0.1f, "remote");
            if (SubDevice.Find(FoeId, 4) != null) Fail("their break message didn't remove it");
        }

        Despawn(FoeId);
        yield return Hold(Still, false, 0.1f, "remote");
        NetGameManager.SendOverride = null;
    }

    IEnumerator SpecialShield()
    {
        NetGameManager gm = NetGameManager.Instance;
        PlayerLoadout loadout = player.GetComponent<PlayerLoadout>();
        if (loadout == null) { Fail("player has no PlayerLoadout"); yield break; }
        CaptureSends();
        const ulong FoeId = 3013;
        int enemyTeam = player.Team == 1 ? 2 : 1;
        loadout.SetSpecial(SpecialType.BubbleShield);
        loadout.SetSpecialPoints(0f);

        // Turf: inking clean floor charges it; inking the same spots again barely does.
        SurfaceInkManager floor = station.GetComponentsInChildren<SurfaceInkManager>().First(m => m.name == "Floor");
        floor.FillRegion(new Rect(0, 0, 1, 1), 0);
        yield return Hold(Still, false, 0.3f, "turf");
        Vector3[] spots = Enumerable.Range(0, 6).Select(i => station.TransformPoint(new Vector3(-9f + i * 3.5f, 3f, -8f))).ToArray();
        foreach (Vector3 spot in spots) Shoot(spot, player.Team, player.OwnerId);
        yield return Hold(Still, false, 1.2f, "turf");
        float fresh = loadout.SpecialPoints;
        foreach (Vector3 spot in spots) Shoot(spot, player.Team, player.OwnerId);
        yield return Hold(Still, false, 1.2f, "turf");
        float repaint = loadout.SpecialPoints - fresh;
        Note($"special +{fresh:F0}p for new turf, +{repaint:F0}p repainting it");
        if (fresh < 1f) Fail("inking neutral turf didn't charge the special");
        if (repaint > fresh * 0.35f) Fail("repainting our own turf charged nearly as much as new turf");

        // Damage dealt: our hits on an enemy count, other people's don't.
        SpawnRemote(FoeId, enemyTeam, station.TransformPoint(new Vector3(-9f, 0.05f, 8f)), "Foe");
        yield return Hold(Still, false, 0.1f, "damage");
        PlayerController foe = gm.GetPlayer(FoeId);
        if (foe == null) { Fail("enemy didn't spawn"); NetGameManager.SendOverride = null; yield break; }
        float before = loadout.SpecialPoints;
        foe.Hitbox.TakeDamage(30f, player.Team, player.OwnerId);
        float ours = loadout.SpecialPoints - before, afterOurs = loadout.SpecialPoints;
        foe.Hitbox.TakeDamage(30f, player.Team, 99999);
        float theirs = loadout.SpecialPoints - afterOurs;
        Note($"special +{ours:F2}p for our 30 damage, +{theirs:F2}p for someone else's");
        if (Mathf.Abs(ours - 30f) > 0.01f || Mathf.Abs(theirs) > 0.001f) Fail("damage dealt didn't charge 1p per damage (ours only)");

        // Full: ready. Using it refills ink, raises the shield (replicated) and spends the charge.
        loadout.SetSpecialPoints(1000f);
        if (!loadout.SpecialReady) Fail("not ready at 1000p");
        yield return Hold(Still, false, 0.1f, "special");
        player.ConsumeInk(0.5f);
        if (!loadout.UseSpecial()) { Fail("couldn't use a ready special"); Despawn(FoeId); NetGameManager.SendOverride = null; yield break; }
        float usedAt = Time.time;
        yield return Hold(Still, false, 0.2f, "shield");
        if (player.InkLevel < 0.99f) Fail($"ink is {player.InkLevel:F2} after the special, not full");
        if (!loadout.ShieldActive || !player.GetNetState(0, 0).shielded) Fail("shield isn't up (or isn't in the network state)");
        if (!loadout.ShieldVisible) Fail("bubble isn't showing");
        if (loadout.SpecialPoints != 0f || loadout.UseSpecial()) Fail("charge wasn't spent");

        // Shielded: hits do nothing, and nothing charges meanwhile.
        float hp = player.Hitbox.Health;
        gm.Receive(NetMsg.Damage, new DamageData { targetSteamId = player.OwnerId, attackerSteamId = FoeId, amount = 50f, fromTeam = enemyTeam }, FoeId);
        yield return Hold(Still, false, 0.1f, "shield");
        if (player.Hitbox.Health < hp) Fail("took damage through the shield");
        loadout.AddSpecialPoints(100f);
        if (loadout.SpecialPoints > 0f) Fail("charged while the special was running");

        // It lasts 5s, then damage applies again.
        yield return HoldUntil(Still, false, 6f, "shield", f => !loadout.ShieldActive);
        float lasted = Time.time - usedAt;
        Note($"shield lasted {lasted:F2}s");
        if (Mathf.Abs(lasted - 5f) > 0.1f) Fail($"shield lasted {lasted:F2}s, not 5s");
        hp = player.Hitbox.Health;
        gm.Receive(NetMsg.Damage, new DamageData { targetSteamId = player.OwnerId, attackerSteamId = FoeId, amount = 20f, fromTeam = enemyTeam }, FoeId);
        yield return Hold(Still, false, 0.1f, "after");
        if (player.Hitbox.Health >= hp) Fail("no damage after the shield ended");

        // A shielded enemy: the shooter's side sends nothing and gains nothing.
        gm.Receive(NetMsg.PlayerState, new PlayerStateData { steamId = FoeId, position = foe.transform.position, moveDir = Vector2.zero, team = enemyTeam, shielded = true }, FoeId);
        yield return Hold(Still, false, 0.1f, "after");
        sent.Clear();
        before = loadout.SpecialPoints;
        foe.Hitbox.TakeDamage(30f, player.Team, player.OwnerId);
        if (sent.Any(m => m.id == NetMsg.Damage) || loadout.SpecialPoints != before) Fail("hit a shielded enemy");

        player.Hitbox.ResetHealth();
        Despawn(FoeId);
        yield return Hold(Still, false, 0.1f, "after");
        NetGameManager.SendOverride = null;
    }

    IEnumerator LoadoutGauges()
    {
        PlayerLoadout loadout = player.GetComponent<PlayerLoadout>();
        LoadoutHUD gauges = FindFirstObjectByType<LoadoutHUD>();
        if (loadout == null || gauges == null) { Fail("no PlayerLoadout or LoadoutHUD"); yield break; }

        player.RefillInk();
        loadout.SetSpecialPoints(500f);
        yield return Hold(Still, false, 1f, "gauges");
        Note($"sub gauge {gauges.SubFill:F2}, special gauge {gauges.SpecialFill:F2}");
        if (Mathf.Abs(gauges.SubFill - player.InkLevel) > 0.03f) Fail("sub gauge doesn't follow the ink");
        if (Mathf.Abs(gauges.SpecialFill - 0.5f) > 0.03f) Fail("special gauge doesn't follow the charge");
        if (gauges.ReadyShown) Fail("READY shown at half charge");
        loadout.SetSpecialPoints(1000f);
        yield return Hold(Still, false, 0.2f, "gauges");
        if (!gauges.ReadyShown) Fail("READY not shown when full");
        loadout.SetSpecialPoints(0f);
    }

    IEnumerator SwimSpeeds()
    {
        SurfaceInkManager floor = station.GetComponentsInChildren<SurfaceInkManager>().First(m => m.name == "Floor");
        int enemyTeam = player.Team == 1 ? 2 : 1;
        var speeds = new Dictionary<string, float>();
        foreach (var (label, team) in new[] { ("own", player.Team), ("ground", 0), ("enemy", enemyTeam) })
        {
            floor.FillRegion(new Rect(0, 0, 1, 1), team);
            yield return Hold(Still, true, 0.3f, "dive " + label); // readback, and into swim form
            yield return Hold(Fwd, true, 0.4f, "swim " + label);
            speeds[label] = player.CurrentSpeed;
            yield return Hold(Back, true, 0.4f, "back " + label); // stay on the station
        }
        floor.FillRegion(new Rect(0, 0, 1, 1), 0);
        Note($"swim speed: own {speeds["own"]:F2}, ground {speeds["ground"]:F2}, enemy {speeds["enemy"]:F2}");
        if (!(speeds["own"] > speeds["ground"] && speeds["ground"] > speeds["enemy"])) Fail("swim speeds aren't own > ground > enemy");
    }

    IEnumerator LobbyMenuList()
    {
        LobbyMenu menu = FindFirstObjectByType<LobbyMenu>();
        if (menu == null) { Fail("no LobbyMenu in the scene"); yield break; }
        yield return Hold(Still, false, 0.1f, "menu");
        if (menu.IsShown) Fail("shown in dev mode");

        menu.ForceShow = true;
        yield return Hold(Still, false, 0.1f, "menu");
        if (!menu.IsShown) Fail("didn't show when forced");
        NetGameManager.LobbyInfo L(string host, int n) => new NetGameManager.LobbyInfo { host = host, players = n, maxPlayers = 8 };
        menu.ShowLobbies(new[] { L("Alpha Tester", 3), L("Beta Tester", 1), L("Gamma Tester", 7) });
        var active = menu.Rows.Where(r => r.gameObject.activeSelf).ToList();
        if (active.Count != 3 || active[1].hostName.text != "Beta Tester" || active[2].players.text != "7/8" || menu.EmptyShown)
            Fail($"rows don't match the lobbies ({active.Count} shown)");
        menu.ShowLobbies(new[] { L("Solo", 1) });
        if (menu.Rows.Count(r => r.gameObject.activeSelf) != 1) Fail("rows weren't reused/hidden for a shorter list");

        // No Steam in dev mode, so joining fails: reported, then the list refreshes (to empty here).
        menu.ForceShow = false; // let it run its own logic for the click
        menu.Rows.First(r => r.gameObject.activeSelf).join.onClick.Invoke();
        Note($"after a failed join: \"{menu.Status}\"");
        if (!menu.EmptyShown || menu.Rows.Any(r => r.gameObject.activeSelf)) Fail("failed join didn't refresh the list");
        yield return Hold(Still, false, 0.1f, "menu");
        if (menu.IsShown) Fail("still shown after un-forcing in dev mode");
        Cursor.lockState = CursorLockMode.Locked;
    }

    IEnumerator MatchFlow()
    {
        NetGameManager gm = NetGameManager.Instance;
        MatchHUD hud = FindFirstObjectByType<MatchHUD>();
        MatchFlowHUD flow = FindFirstObjectByType<MatchFlowHUD>();
        HostMenu menu = FindFirstObjectByType<HostMenu>();
        PlayerLoadout loadout = player.GetComponent<PlayerLoadout>();
        if (hud == null || flow == null || menu == null || loadout == null) { Fail("missing MatchHUD, MatchFlowHUD, HostMenu or PlayerLoadout"); yield break; }
        float Flat(Vector3 v) => new Vector2(v.x, v.z).magnitude;
        bool Broadcast(MatchPhase phase) => sent.Any(m => m.id == NetMsg.MatchEvent && ((MatchEventData)m.data).phase == phase);
        CaptureSends();
        gm.SetMatchTimings(1f, 3f, 1.5f, 0.6f, 5.5f);
        SurfaceInkManager floor = station.GetComponentsInChildren<SurfaceInkManager>().First(m => m.name == "Floor");
        floor.FillRegion(new Rect(0, 0, 1, 1), player.Team);
        loadout.SetSpecialPoints(600f);
        hud.ShowPercentages = true;
        yield return Hold(Still, false, 0.3f, "free");

        // Start: everyone to their spawn, locked, map and loadout reset, host features off; new colours.
        int pairBefore = gm.ColourPair;
        gm.StartGame();
        frames.Clear(); // the respawn teleport
        Vector3 start = player.transform.position;
        if (gm.Phase != MatchPhase.Countdown || !Broadcast(MatchPhase.Countdown)) Fail("countdown didn't start (or wasn't broadcast)");
        bool pairSent = sent.Any(m => m.id == NetMsg.MatchEvent && m.data is MatchEventData d && d.phase == MatchPhase.Countdown && d.colourPair == gm.ColourPair);
        Note($"colours {pairBefore} -> {gm.ColourPair} ({gm.ColourPairName}) for the new game");
        if (gm.ColourPair == pairBefore || !pairSent) Fail("a new game didn't pick (and send) new team colours");
        yield return Hold(Fwd, true, 0.5f, "countdown");
        Note($"countdown banner \"{flow.BannerText}\"");
        if (flow.BannerText != "1") Fail("countdown number not shown");
        if (!InputGate.MatchLocked || Flat(player.transform.position - start) > 0.05f || player.IsSquid) Fail("could move or swim during the countdown");
        if (loadout.SpecialPoints != 0f || player.InkLevel < 0.999f) Fail("special/ink not reset for the match");
        if (floor.getSurfaceTeam(new Vector2(0.5f, 0.5f)) != 0) Fail("map wasn't reset");
        if (hud.PercentagesShown) Fail("percentages shown during a match");
        menu.SetOpen(true);
        yield return null;
        if (menu.ResetButton.interactable || menu.TeamsButton.interactable || menu.PercentButton.interactable || menu.StartButtonText != "End match")
            Fail("host menu still offers free-roam features mid-match");
        menu.SetOpen(false);

        // Playing: GO!, free to move.
        yield return HoldUntil(Still, false, 2f, "play", f => gm.Phase == MatchPhase.Playing);
        yield return null; // the HUD may have updated before the phase changed that frame
        if (flow.BannerText != "GO!" || InputGate.MatchLocked) Fail("no GO! (or still locked) when play starts");
        Vector3 before = player.transform.position;
        yield return Hold(Fwd, false, 0.4f, "play");
        if (Flat(player.transform.position - before) < 0.3f) Fail("can't move once playing");
        floor.FillRegion(new Rect(0, 0, 1, 0.6f), player.Team); // our win

        // Final stretch: no banner (music will announce it), and the coverage bar fades out.
        bool stretchEvent = false;
        Action onStretch = () => stretchEvent = true;
        NetGameManager.FinalStretchStarted += onStretch;
        yield return HoldUntil(Still, false, 3f, "play", f => gm.InFinalStretch);
        yield return Hold(Still, false, 0.1f, "final");
        NetGameManager.FinalStretchStarted -= onStretch;
        if (!stretchEvent) Fail("FinalStretchStarted wasn't raised (the music hook)");
        if (flow.BannerText != "") Fail($"final stretch shows a banner (\"{flow.BannerText}\")");
        yield return Hold(Still, false, 1.1f, "final");
        Note($"turf bar alpha {hud.TurfBarAlpha:F2} 1.2s into the final stretch");
        if (hud.TurfBarAlpha > 0.05f) Fail("coverage bar didn't fade out for the final stretch");

        // Time's up: locked again.
        yield return HoldUntil(Still, false, 2f, "timesup", f => gm.Phase == MatchPhase.TimesUp);
        yield return Hold(Fwd, false, 0.2f, "timesup");
        if (flow.BannerText != "TIME'S UP!" || !InputGate.MatchLocked) Fail("no TIME'S UP (or not locked)");

        // Results: the host's score for everyone, overhead view, suspense fill, then the real split.
        yield return HoldUntil(Still, false, 4f, "results", f => gm.Phase == MatchPhase.Results);
        Vector3Int r = gm.MatchResult;
        Note($"result alpha {r.x}, beta {r.y}, neutral {r.z}");
        var resultMsg = sent.LastOrDefault(m => m.id == NetMsg.MatchEvent && ((MatchEventData)m.data).phase == MatchPhase.Results);
        if (resultMsg.data == null || ((MatchEventData)resultMsg.data).alphaScore != r.x) Fail("results weren't broadcast with the score");
        int ours = player.Team == 1 ? r.x : r.y, theirs = player.Team == 1 ? r.y : r.x;
        if (ours <= theirs) Fail("our painted turf didn't win");
        yield return Hold(Still, false, 0.3f, "results");
        if (!flow.OverheadActive) Fail("no overhead view");
        yield return Hold(Still, false, 2.6f, "results"); // into the pause
        float alphaShare = r.x / (float)(r.x + r.y);
        float suspense = Mathf.Max(0.2f, 0.8f * Mathf.Min(alphaShare, 1f - alphaShare));
        Note($"suspense fill {flow.ResultAlphaShown:F2}/{flow.ResultBetaShown:F2} (expected {suspense:F2} each)");
        if (Mathf.Abs(flow.ResultAlphaShown - suspense) > 0.01f || Mathf.Abs(flow.ResultBetaShown - suspense) > 0.01f) Fail("bar didn't hold both sides at the suspense point");
        if (flow.ResultsRevealed) Fail("revealed before the pause ended");
        yield return Hold(Still, false, 1f, "results");
        string winner = player.Team == 1 ? "ALPHA TEAM WINS!" : "BETA TEAM WINS!";
        Note($"revealed {flow.ResultAlphaShown:F3}/{flow.ResultBetaShown:F3}, banner \"{flow.BannerText}\"");
        if (Mathf.Abs(flow.ResultAlphaShown - alphaShare) > 0.001f || Mathf.Abs(flow.ResultBetaShown - (1f - alphaShare)) > 0.001f) Fail("bar didn't settle on the real split");
        if (flow.BannerText != winner) Fail("winner not announced");

        // Back to free roam: unlocked, player view, bar and the host's percentages back.
        yield return HoldUntil(Still, false, 3f, "free", f => gm.Phase == MatchPhase.FreeRoam);
        yield return Hold(Still, false, 0.8f, "free");
        if (InputGate.MatchLocked || flow.OverheadActive) Fail("still locked (or overhead) after the results");
        if (hud.TurfBarAlpha < 0.95f || !hud.PercentagesShown) Fail("coverage bar or percentages didn't come back");

        // The host can abandon a match.
        gm.StartGame();
        frames.Clear();
        yield return Hold(Still, false, 0.2f, "abort");
        gm.EndMatch();
        yield return Hold(Still, false, 0.1f, "abort");
        if (gm.Phase != MatchPhase.FreeRoam || InputGate.MatchLocked) Fail("End match didn't return to free roam");

        hud.ShowPercentages = false;
        floor.FillRegion(new Rect(0, 0, 1, 1), 0);
        gm.SetMatchTimings(3f, 180f, 60f, 2.5f, 15f);
        NetGameManager.SendOverride = null;
    }

    IEnumerator LateJoin()
    {
        NetGameManager gm = NetGameManager.Instance;
        const ulong LateId = 3020;
        CaptureSends();
        gm.SetMatchTimings(0.3f, 60f, 30f, 0.5f, 2f);
        gm.StartGame();
        frames.Clear();
        yield return HoldUntil(Still, false, 2f, "match", f => gm.Phase == MatchPhase.Playing);
        sent.Clear();

        SpawnRemote(LateId, 2, station.TransformPoint(new Vector3(-9f, 0.05f, 8f)), "Latecomer");
        yield return Hold(Still, false, 0.2f, "join");
        PlayerController late = gm.GetPlayer(LateId);
        if (late == null) { Fail("late joiner didn't spawn"); gm.EndMatch(); NetGameManager.SendOverride = null; yield break; }
        bool rosterSaysSpectate = sent.Any(m => m.id == NetMsg.TeamAssign && m.data is TeamAssignData t
            && Array.IndexOf(t.ids, LateId) is int i && i >= 0 && t.roles[i] == (int)PlayerRole.Spectator);
        var phaseMsg = sent.FirstOrDefault(m => m.id == NetMsg.MatchEvent && m.to == LateId);
        var phaseData = phaseMsg.data as MatchEventData;
        Note($"late joiner role {late.Role}; told phase {phaseData?.phase} with {phaseData?.duration:F1}s left");
        if (late.Role != PlayerRole.Spectator || !rosterSaysSpectate) Fail("late joiner wasn't made a spectator");
        if (phaseData == null || phaseData.phase != MatchPhase.Playing || !phaseData.lateJoin || Mathf.Abs(phaseData.duration - gm.PhaseTimeRemaining) > 0.5f)
            Fail("late joiner wasn't told the current phase and time left");
        if (late.Team != 0) Fail("a spectator still has a team");

        // The match ends: back to the team they joined on.
        sent.Clear();
        gm.EndMatch();
        yield return Hold(Still, false, 0.2f, "after");
        if (late.Role != PlayerRole.Beta || !sent.Any(m => m.id == NetMsg.TeamAssign)) Fail($"late joiner didn't get their team back ({late.Role})");
        if (gm.IsLateJoiner(LateId)) Fail("still marked as a late joiner");

        Despawn(LateId);
        yield return Hold(Still, false, 0.1f, "after");
        gm.SetMatchTimings(3f, 180f, 60f, 2.5f, 15f);
        NetGameManager.SendOverride = null;
    }

    IEnumerator SpectatorView()
    {
        NetGameManager gm = NetGameManager.Instance;
        MatchFlowHUD flow = FindFirstObjectByType<MatchFlowHUD>();
        if (flow == null) { Fail("no MatchFlowHUD"); yield break; }
        NetGameManager.SendOverride = (id, data, target) => { };
        int team = player.Team;
        if (flow.OverheadActive) Fail("overhead view while playing");

        // The host moves us to spectate: our player goes away, the overhead view takes over.
        gm.Receive(NetMsg.TeamAssign, new TeamAssignData { ids = new[] { player.OwnerId }, roles = new[] { (int)PlayerRole.Spectator } }, 0);
        yield return Hold(Still, false, 0.2f, "spectate");
        if (player.gameObject.activeInHierarchy) Fail("our player is still in the level");
        if (!flow.OverheadActive || !flow.SpectatingShown) Fail("no overhead view (or label) while spectating");

        gm.Receive(NetMsg.TeamAssign, new TeamAssignData { ids = new[] { player.OwnerId }, roles = new[] { team } }, 0);
        yield return Hold(Still, false, 0.2f, "back");
        frames.Clear(); // back at our spawn
        if (!player.gameObject.activeInHierarchy || flow.OverheadActive || flow.SpectatingShown) Fail("overhead view didn't hand back to our player");
        NetGameManager.SendOverride = null;
    }

    IEnumerator FeetInk()
    {
        WeaponShooter weapon = player.GetComponentInChildren<WeaponShooter>();
        if (weapon == null) { Fail("no WeaponShooter"); yield break; }
        NetGameManager.SendOverride = (id, data, target) => { };
        SurfaceInkManager floor = station.GetComponentsInChildren<SurfaceInkManager>().First(m => m.name == "Floor");
        floor.FillRegion(new Rect(0, 0, 1, 1), 0);
        player.RefillInk();
        yield return Hold(Still, false, 0.3f, "ready");

        int before = weapon.FeetShots;
        weapon.ScriptedFire = true;
        yield return Hold(Still, false, 1.3f, "fire");
        weapon.ScriptedFire = false;
        int drops = weapon.FeetShots - before;
        yield return Hold(Still, false, 0.6f, "land");

        int underfoot = -1;
        if (Physics.Raycast(player.BodyCenter, Vector3.down, out RaycastHit hit, 3f, PhysicsLayers.Environment, QueryTriggerInteraction.Ignore))
        {
            SurfaceInkManager ink = hit.collider.GetComponent<SurfaceInkManager>();
            if (ink != null) underfoot = ink.getSurfaceTeam(ink.UVFromHit(hit));
        }
        Note($"{drops} feet drops in 1.3s; ink underfoot team {underfoot}");
        if (drops < 3 || drops > 4) Fail($"expected 3-4 feet drops in 1.3s (every 0.4s), got {drops}");
        if (underfoot != player.Team) Fail("not standing in our own ink after shooting");
        floor.FillRegion(new Rect(0, 0, 1, 1), 0);
        NetGameManager.SendOverride = null;
    }

    IEnumerator WeaponVariety()
    {
        PlayerLoadout loadout = player.GetComponent<PlayerLoadout>();
        if (loadout == null || loadout.Weapons.Count < 2) { Fail("need two weapons"); yield break; }
        NetGameManager.SendOverride = (id, data, target) => { };
        var shots = new int[2];
        var speed = new float[2];
        var maxYaw = new float[2];
        for (int w = 0; w < 2; w++)
        {
            loadout.SetMainWeapon(w);
            player.RefillInk();
            yield return Hold(Still, false, 0.3f, "equip");
            WeaponShooter weapon = loadout.CurrentWeapon as WeaponShooter;
            if (weapon == null) { Fail($"{loadout.Weapons[w].DisplayName} isn't a gun"); break; }
            int index = w;
            Action<Vector3, Quaternion> onShot = (v, aim) =>
            {
                speed[index] = v.magnitude;
                Vector3 flatAim = Vector3.ProjectOnPlane(aim * Vector3.forward, Vector3.up);
                maxYaw[index] = Mathf.Max(maxYaw[index], Mathf.Abs(Vector3.SignedAngle(flatAim, Vector3.ProjectOnPlane(v, Vector3.up), Vector3.up)));
            };
            weapon.MainShotFired += onShot;
            int before = weapon.MainShots;
            weapon.ScriptedFire = true;
            yield return Hold(Still, false, 1f, "fire " + weapon.DisplayName);
            weapon.ScriptedFire = false;
            weapon.MainShotFired -= onShot;
            shots[w] = weapon.MainShots - before;
        }
        Note($"Airspray SE: {shots[0]} shots/s, speed {speed[0]:F1}, max yaw {maxYaw[0]:F1}°; Inkshot: {shots[1]} shots/s, speed {speed[1]:F1}, max yaw {maxYaw[1]:F1}°");
        if (loadout.Weapons[0].DisplayName != "Airspray SE" || loadout.Weapons[1].DisplayName != "Inkshot") Fail("weapons aren't Airspray SE and Inkshot");
        if (shots[1] >= shots[0] * 0.8f) Fail("Inkshot doesn't fire slower");
        if (speed[1] < speed[0] * 1.2f) Fail("Inkshot doesn't reach further");
        if (maxYaw[1] >= maxYaw[0] * 0.5f) Fail("Inkshot's spread isn't much narrower");
        loadout.SetMainWeapon(0);
        yield return Hold(Still, false, 0.5f, "settle"); // let the shots land
        NetGameManager.SendOverride = null;
    }

    IEnumerator WeaponPrefabs()
    {
        NetGameManager gm = NetGameManager.Instance;
        PlayerLoadout loadout = player.GetComponent<PlayerLoadout>();
        if (loadout == null) { Fail("player has no PlayerLoadout"); yield break; }
        NetGameManager.SendOverride = (id, data, target) => { };
        const ulong MateId = 3040;

        // Switching replaces the instance in our hands with the other prefab.
        loadout.SetMainWeapon(1);
        yield return null;
        Weapon held = loadout.CurrentWeapon;
        Transform mount = held != null ? held.transform.parent : null;
        int inHand = mount == null ? 0 : mount.GetComponentsInChildren<Weapon>().Length;
        Note($"holding {held?.DisplayName} under {mount?.name} ({inHand} weapon(s) there)");
        if (held == null || held.DisplayName != "Inkshot" || inHand != 1) Fail("switching didn't swap the weapon in our hands");
        if (player.GetNetState(0, 0).weapon != 1) Fail("our weapon choice isn't in our state");

        // Jumping doesn't carry into the shots; running still does.
        WeaponShooter gun = held as WeaponShooter;
        player.RefillInk();
        player.PressJump();
        gun.ScriptedFire = true;
        float risingY = 0f, inheritedY = 0f;
        for (int i = 0; i < 20; i++)
        {
            yield return null;
            Record("jumpfire");
            if (player.VerticalVelocity > risingY) { risingY = player.VerticalVelocity; inheritedY = gun.LastInherited.y; }
        }
        gun.ScriptedFire = false;
        Note($"rising at {risingY:F1} m/s, shots inherited {inheritedY:F2} m/s vertically");
        if (risingY < 1f) Fail("never left the ground");
        if (inheritedY != 0f) Fail("shots inherited our vertical velocity");
        yield return Hold(Still, false, 1f, "land");

        // A teammate's copy shows the weapon their state says, as a model only.
        SpawnRemote(MateId, player.Team, station.TransformPoint(new Vector3(-9f, 0.05f, 8f)), "Mate");
        yield return Hold(Still, false, 0.2f, "remote");
        PlayerController mate = gm.GetPlayer(MateId);
        PlayerLoadout mateLoadout = mate != null ? mate.GetComponent<PlayerLoadout>() : null;
        if (mateLoadout == null || mateLoadout.CurrentWeapon == null || mateLoadout.CurrentWeapon.DisplayName != "Airspray SE") Fail("remote copy didn't start with the default weapon");
        gm.Receive(NetMsg.PlayerState, new PlayerStateData { steamId = MateId, position = mate.transform.position, moveDir = Vector2.zero, team = player.Team, weapon = 1 }, MateId);
        yield return Hold(Still, false, 0.2f, "remote");
        Weapon theirs = mateLoadout != null ? mateLoadout.CurrentWeapon : null;
        if (theirs == null || theirs.DisplayName != "Inkshot") Fail("remote copy didn't switch to their Inkshot");
        else if (theirs.enabled) Fail("remote copy's weapon is live (it should only be a model)");

        Despawn(MateId);
        loadout.SetMainWeapon(0);
        yield return Hold(Still, false, 0.1f, "remote");
        NetGameManager.SendOverride = null;
    }

    IEnumerator LoadoutMenuPicks()
    {
        NetGameManager gm = NetGameManager.Instance;
        LoadoutMenu menu = FindFirstObjectByType<LoadoutMenu>();
        PlayerLoadout loadout = player.GetComponent<PlayerLoadout>();
        LoadoutHUD gauges = FindFirstObjectByType<LoadoutHUD>();
        if (menu == null || loadout == null) { Fail("missing LoadoutMenu or PlayerLoadout"); yield break; }
        NetGameManager.SendOverride = (id, data, target) => { };

        menu.SetOpen(true);
        yield return null;
        if (!menu.IsOpen || !InputGate.Blocked) Fail("didn't open (or didn't block gameplay input)");
        menu.WeaponButtons[1].onClick.Invoke();
        menu.SubButtons[1].onClick.Invoke();
        menu.SpecialButtons[1].onClick.Invoke();
        yield return null;
        if (loadout.Special != SpecialType.InkStrike || PlayerPrefs.GetInt("loadout.special", -1) != (int)SpecialType.InkStrike) Fail("picking InkStrike didn't select (and remember) it");
        if (loadout.WeaponIndex != 1 || loadout.CurrentWeapon == null || loadout.CurrentWeapon.DisplayName != "Inkshot") Fail("picking Inkshot didn't equip it");
        if (loadout.Sub != SubType.Sprinkler || Mathf.Abs(loadout.SubInkCost - loadout.SprinklerPrefab.InkCost) > 0.001f) Fail("picking the sprinkler didn't select it");
        if (PlayerPrefs.GetInt("loadout.weapon", -1) != 1 || PlayerPrefs.GetInt("loadout.sub", -1) != (int)SubType.Sprinkler) Fail("choices weren't remembered");
        menu.SetOpen(false);
        yield return Hold(Still, false, 0.1f, "menu");
        if (menu.IsOpen || InputGate.Blocked) Fail("didn't close");
        if (gauges != null && gauges.SubName != "SPRINKLER") Fail("sub gauge doesn't show the sprinkler");

        // Not during a match; one in progress closes it.
        gm.SetMatchTimings(5f, 60f, 30f, 0.5f, 2f);
        menu.SetOpen(true);
        gm.StartGame();
        frames.Clear();
        yield return Hold(Still, false, 0.1f, "match");
        if (menu.IsOpen) Fail("stayed open when a match started");
        menu.SetOpen(true);
        if (menu.IsOpen) Fail("opened during a match");
        gm.EndMatch();
        gm.SetMatchTimings(3f, 180f, 60f, 2.5f, 15f);
        yield return Hold(Still, false, 0.1f, "match");

        if (gauges != null && gauges.SpecialName != "INKSTRIKE") Fail("special gauge doesn't show InkStrike");

        loadout.SetMainWeapon(0);
        loadout.SetSub(SubType.Beacon);
        loadout.SetSpecial(SpecialType.BubbleShield);
        NetGameManager.SendOverride = null;
    }

    IEnumerator InkStrikeSpecial()
    {
        NetGameManager gm = NetGameManager.Instance;
        PlayerLoadout loadout = player.GetComponent<PlayerLoadout>();
        MapScreen map = FindFirstObjectByType<MapScreen>();
        if (loadout == null || map == null || loadout.InkStrikePrefab == null) { Fail("missing PlayerLoadout, MapScreen or the InkStrike prefab"); yield break; }
        CaptureSends();
        const ulong FoeIn = 3050, FoeOut = 3051, MateIn = 3052, FoeEdge = 3053, FoeHigh = 3054;
        int enemyTeam = player.Team == 1 ? 2 : 1;
        SurfaceInkManager floor = station.GetComponentsInChildren<SurfaceInkManager>().First(m => m.name == "Floor");
        floor.FillRegion(new Rect(0, 0, 1, 1), 0);
        Vector3 target = station.TransformPoint(new Vector3(6f, 0f, 6f));
        float radius = loadout.InkStrikePrefab.Radius;
        SpawnRemote(FoeIn, enemyTeam, target + new Vector3(1f, 0.05f, 1f), "FoeIn");
        SpawnRemote(FoeOut, enemyTeam, target + new Vector3(radius + 3f, 0.05f, 0f), "FoeOut");
        SpawnRemote(MateIn, player.Team, target + new Vector3(-1f, 0.05f, 0f), "MateIn");
        SpawnRemote(FoeEdge, enemyTeam, target + new Vector3(0f, 0.05f, -radius * 0.92f), "FoeEdge");
        SpawnRemote(FoeHigh, enemyTeam, target + new Vector3(0.5f, loadout.InkStrikePrefab.Height * 0.6f, 0f), "FoeHigh"); // up the column
        loadout.SetSpecial(SpecialType.InkStrike);
        loadout.SetSpecialPoints(1000f);
        yield return Hold(Still, false, 0.3f, "ready");
        player.ConsumeInk(0.5f);

        // Using it opens the map to aim; cancelling keeps the charge.
        if (!loadout.UseSpecial() || !map.IsOpen || !map.IsTargeting) Fail("using it didn't open the map to aim");
        map.CancelTargeting();
        yield return null;
        if (map.IsOpen || loadout.Targeting || loadout.SpecialPoints < 1000f) Fail("cancelling didn't close the map (or spent the charge)");

        // Pick the spot on the map: it's marked, then strikes after the delay.
        loadout.UseSpecial();
        yield return null;
        if (!map.PickTargetAt(map.MapPosition(target, false))) Fail("couldn't pick the spot on the map");
        yield return null;
        InkStrike strike = FindFirstObjectByType<InkStrike>();
        if (strike == null) { Fail("no strike after picking"); loadout.SetSpecial(SpecialType.BubbleShield); NetGameManager.SendOverride = null; yield break; }
        float aimError = new Vector2(strike.Target.x - target.x, strike.Target.z - target.z).magnitude;
        Note($"strike {aimError:F2}m from the spot picked on the map");
        if (aimError > 0.5f) Fail("the strike isn't where the map was clicked");
        if (loadout.SpecialPoints != 0f || player.InkLevel < 0.99f) Fail("didn't spend the charge and refill ink");
        if (!sent.Any(m => m.id == NetMsg.InkStrike && m.data is InkStrikeData d && Vector3.Distance(d.position, strike.Target) < 0.01f)) Fail("strike wasn't broadcast");
        if (strike.Struck) Fail("struck without the warning delay");
        sent.Clear();
        float markedAt = Time.time;
        yield return HoldUntil(Still, false, 3f, "strike", f => strike == null || strike.Struck);
        Note($"struck {Time.time - markedAt:F2}s after being marked");
        if (Mathf.Abs(Time.time - markedAt - loadout.InkStrikePrefab.Delay) > 0.1f) Fail("didn't strike after its delay");
        float strikeWidth = strike.Width;

        // It spreads out from the middle: halfway, the core is hit but the edge isn't yet.
        yield return HoldUntil(Still, false, strike.AttackTime + 1f, "spread", f => strike == null || strike.Front >= radius * 0.5f);
        bool coreHit = sent.Any(m => m.id == NetMsg.Damage && m.to == FoeIn), edgeHit = sent.Any(m => m.id == NetMsg.Damage && m.to == FoeEdge);
        Note($"halfway out: core {(coreHit ? "hit" : "not hit")}, edge {(edgeHit ? "hit" : "not hit")}");
        if (!coreHit || edgeHit) Fail("the damage didn't spread out from the middle");
        float halfwayWidth = strike.Width;
        yield return HoldUntil(Still, false, loadout.InkStrikePrefab.AttackTime + 0.3f, "after", f => strike.Front >= radius);
        yield return Hold(Still, false, 0.2f, "after");
        float fullWidth = strike.Width;
        Note($"column radius {strikeWidth:F2}m as it strikes, {halfwayWidth:F2}m halfway out, {fullWidth:F2}m at full size (thin {strike.TightRadius:F2}, full {strike.FullWidth:F2})");
        if (strikeWidth > strike.TightRadius * 0.5f) Fail("the column didn't grow out of nothing");
        if (halfwayWidth <= strike.TightRadius || halfwayWidth >= strike.FullWidth) Fail("the column wasn't widening from thin to full");
        if (Mathf.Abs(fullWidth - strike.FullWidth) > 0.05f) Fail("the column didn't reach its full size");
        yield return Hold(Still, false, 0.3f, "after");

        // Inside is our ink; enemies inside got lethal damage, nobody else did.
        int inked = 0, probes = 0;
        for (int i = 0; i < 24; i++)
        {
            Vector3 probe = target + Quaternion.Euler(0f, i * 15f, 0f) * Vector3.forward * radius * (0.2f + 0.6f * (i % 3) / 2f) + Vector3.up;
            if (!Physics.Raycast(probe, Vector3.down, out RaycastHit hit, 3f, PhysicsLayers.Environment, QueryTriggerInteraction.Ignore)) continue;
            SurfaceInkManager ink = hit.collider.GetComponent<SurfaceInkManager>();
            if (ink == null) continue;
            probes++;
            if (ink.getSurfaceTeam(ink.UVFromHit(hit)) == player.Team) inked++;
        }
        DamageData Hit(ulong id) => sent.Where(m => m.id == NetMsg.Damage && m.to == id).Select(m => m.data as DamageData).FirstOrDefault();
        Note($"{inked}/{probes} points inside are our ink; damage to FoeIn {Hit(FoeIn)?.amount ?? 0f:0}, FoeHigh {Hit(FoeHigh)?.amount ?? 0f:0}, FoeEdge {Hit(FoeEdge)?.amount ?? 0f:0}, FoeOut {Hit(FoeOut)?.amount ?? 0f:0}, MateIn {Hit(MateIn)?.amount ?? 0f:0}");
        if (probes == 0 || inked < probes * 0.9f) Fail("the area isn't covered in our ink");
        if (Hit(FoeIn) == null || Hit(FoeIn).amount < 100f) Fail("the enemy in the core wasn't splatted");
        if (Hit(FoeHigh) == null || Hit(FoeHigh).amount < 100f) Fail("the enemy up the column wasn't splatted");
        if (Hit(FoeEdge) == null || Hit(FoeEdge).amount <= 0f || Hit(FoeEdge).amount >= 100f) Fail("the enemy at the edge should be hit, but not fatally");
        if (Hit(FoeOut) != null || Hit(MateIn) != null) Fail("hit someone outside the area (or a teammate)");

        // The whole column, even aimed at the top of something tall: down to the floor around a bare
        // 10m block, its sides all the way down, and whoever stands beside it.
        Transform wallStation = GameObject.Find(Name(Station.NeutralWall)).transform;
        SurfaceInkManager wall = wallStation.GetComponentsInChildren<SurfaceInkManager>().First(m => m.name == "Wall");
        Bounds wb = wall.GetComponent<Renderer>().bounds;
        Vector3 onTop = new Vector3(wb.center.x, wb.max.y, wb.center.z);
        Vector3 beside = new Vector3(wb.center.x, wb.min.y, wb.min.z - 1f);
        if (Physics.Raycast(beside + Vector3.up, Vector3.down, out RaycastHit ground, 3f, PhysicsLayers.Environment, QueryTriggerInteraction.Ignore)) beside = ground.point;
        const ulong FoeBelow = 3055;
        SpawnRemote(FoeBelow, enemyTeam, beside + Vector3.up * 0.05f, "FoeBelow");
        yield return Hold(Still, false, 0.1f, "column");
        sent.Clear();
        loadout.SetSpecialPoints(1000f);
        loadout.LaunchInkStrike(onTop);
        InkStrike tall = FindObjectsByType<InkStrike>(FindObjectsSortMode.None).FirstOrDefault(k => k.IsOwnedLocally && !k.Struck);
        Note($"aimed at the block's top (y {onTop.y:0.0}); column bottom {(tall != null ? tall.Bottom : float.NaN):0.0}, floor {beside.y:0.0}");
        if (tall == null || Mathf.Abs(tall.Bottom - beside.y) > 0.2f) Fail("the column doesn't reach down to the floor");
        yield return Hold(Still, false, loadout.InkStrikePrefab.Delay + loadout.InkStrikePrefab.AttackTime + 0.6f, "column");
        if (strike != null && strike.Width > 0.01f) Fail($"the first column didn't shrink back to nothing (radius {strike.Width:F2}m)");
        int wallInked = 0, wallProbes = 0;
        for (float h = 1f; h < wb.size.y; h += 2f)
        for (int side = -1; side <= 1; side++)
        {
            Vector3 from = new Vector3(wb.center.x + side * 2f, beside.y + h, wb.min.z - 1f);
            if (!Physics.Raycast(from, Vector3.forward, out RaycastHit hit, 2f, PhysicsLayers.Environment, QueryTriggerInteraction.Ignore) || hit.collider.GetComponent<SurfaceInkManager>() != wall) continue;
            wallProbes++;
            if (wall.getSurfaceTeam(wall.UVFromHit(hit)) == player.Team) wallInked++;
        }
        int floorInked = 0, floorProbes = 0;
        foreach (Vector3 offset in new[] { Vector3.left, Vector3.right, Vector3.forward, Vector3.back })
        {
            Vector3 probe = onTop + offset * 5f; // between the block (4m out) and the column's edge
            if (!Physics.Raycast(new Vector3(probe.x, beside.y + 1f, probe.z), Vector3.down, out RaycastHit hit, 3f, PhysicsLayers.Environment, QueryTriggerInteraction.Ignore)) continue;
            SurfaceInkManager ink = hit.collider.GetComponent<SurfaceInkManager>();
            if (ink == null) continue;
            floorProbes++;
            if (ink.getSurfaceTeam(ink.UVFromHit(hit)) == player.Team) floorInked++;
        }
        float belowHit = sent.Where(m => m.id == NetMsg.Damage && m.to == FoeBelow).Select(m => (m.data as DamageData).amount).FirstOrDefault();
        Note($"{wallInked}/{wallProbes} points down the block's side and {floorInked}/{floorProbes} on the floor around it are our ink; the enemy beside it took {belowHit:0}");
        if (wallProbes == 0 || wallInked < wallProbes) Fail("didn't ink the block's side all the way down");
        if (floorProbes == 0 || floorInked < floorProbes) Fail("didn't ink the floor around the block");
        if (belowHit <= 0f) Fail("didn't hit the enemy on the floor below");
        Despawn(FoeBelow);
        foreach (SurfaceInkManager ink in wallStation.GetComponentsInChildren<SurfaceInkManager>()) // back as the scene set it up
        {
            ink.FillRegion(new Rect(0, 0, 1, 1), 0);
            TestInkFill fill = ink.GetComponent<TestInkFill>();
            if (fill != null) foreach (TestInkFill.Region r in fill.regions) ink.FillRegion(r.uv, r.team);
        }

        // Theirs: marker and blast only, nothing sent from here.
        sent.Clear();
        gm.Receive(NetMsg.InkStrike, new InkStrikeData { ownerId = FoeOut, team = enemyTeam, position = station.TransformPoint(new Vector3(-6f, 0f, 6f)) }, FoeOut);
        yield return Hold(Still, false, 0.1f, "remote");
        InkStrike theirs = FindObjectsByType<InkStrike>(FindObjectsSortMode.None).FirstOrDefault(k => !k.IsOwnedLocally);
        if (theirs == null) Fail("their strike wasn't shown");
        yield return Hold(Still, false, theirs != null ? theirs.Delay + theirs.AttackTime + 0.3f : 1.2f, "remote");
        if (sent.Any(m => m.id == NetMsg.Damage || m.id == NetMsg.Splat)) Fail("their strike was painted/damaged from here");

        foreach (ulong id in new[] { FoeIn, FoeOut, MateIn, FoeEdge, FoeHigh }) Despawn(id);
        loadout.SetSpecial(SpecialType.BubbleShield);
        yield return Hold(Still, false, 0.6f, "cleanup");
        floor.FillRegion(new Rect(0, 0, 1, 1), 0);
        NetGameManager.SendOverride = null;
    }

    IEnumerator SprinklerSub()
    {
        NetGameManager gm = NetGameManager.Instance;
        PlayerLoadout loadout = player.GetComponent<PlayerLoadout>();
        if (loadout == null) { Fail("player has no PlayerLoadout"); yield break; }
        CaptureSends();
        SubDevice.RemoveAll();
        const ulong FoeId = 3030;
        int enemyTeam = player.Team == 1 ? 2 : 1;
        SurfaceInkManager floor = station.GetComponentsInChildren<SurfaceInkManager>().First(m => m.name == "Floor");
        floor.FillRegion(new Rect(0, 0, 1, 1), 0);
        loadout.SetSub(SubType.Sprinkler);
        player.RefillInk();
        yield return Hold(Still, false, 0.3f, "ready");

        // Thrown: costs 60%, flies, then sticks to the floor.
        if (!loadout.UseSub()) { Fail("couldn't throw a sprinkler"); loadout.SetSub(SubType.Beacon); NetGameManager.SendOverride = null; yield break; }
        Sprinkler s = SubDevice.All.OfType<Sprinkler>().First(d => d.OwnerId == player.OwnerId);
        Note($"ink after throwing {player.InkLevel:F2}");
        if (Mathf.Abs(player.InkLevel - (1f - loadout.SprinklerPrefab.InkCost)) > 0.01f) Fail($"didn't cost {loadout.SprinklerPrefab.InkCost * 100f:0}% ink");
        if (s.Landed) Fail("landed the moment it was thrown");
        if (!sent.Any(m => m.id == NetMsg.SubSpawn && m.data is SubData d && !d.landed && d.subType == (int)SubType.Sprinkler)) Fail("throw wasn't broadcast");
        yield return HoldUntil(Still, false, 3f, "throw", f => s == null || s.Landed);
        if (s == null || !s.Landed) { Fail("never landed"); loadout.SetSub(SubType.Beacon); NetGameManager.SendOverride = null; yield break; }
        float thrownTo = Vector3.Distance(s.transform.position, player.transform.position);
        Note($"stuck {thrownTo:F1}m away, tilt {Vector3.Angle(s.transform.up, Vector3.up):F0}°, {s.TimeLeft:F1}s to live");
        if (Vector3.Angle(s.transform.up, Vector3.up) > 5f) Fail("not stuck upright on the floor");
        if (!sent.Any(m => m.id == NetMsg.SubSpawn && m.data is SubData d && d.landed)) Fail("landing wasn't broadcast");
        if (Mathf.Abs(s.TimeLeft - s.Lifetime) > 0.2f) Fail("doesn't last its lifetime from landing");

        // Spraying, winding down.
        float spin = s.SpinSpeed, shotSpeed = s.ShotSpeed, gap = s.FireInterval;
        int shots = s.ShotsFired;
        yield return Hold(Still, false, 3f, "spray");
        Note($"after 3s: spin {spin:F0}->{s.SpinSpeed:F0}°/s, shot speed {shotSpeed:F1}->{s.ShotSpeed:F1}, gap {gap:F2}->{s.FireInterval:F2}s, {s.ShotsFired - shots} volleys");
        if (s.ShotsFired - shots < 5) Fail("barely fired");
        if (!(s.SpinSpeed < spin && s.ShotSpeed < shotSpeed && s.FireInterval > gap)) Fail("didn't wind down");
        int inked = 0;
        for (int i = 0; i < 16 * 6; i++) // rings 1-6m out
        {
            Vector3 probe = s.transform.position + Quaternion.Euler(0f, i * 22.5f, 0f) * Vector3.forward * (1 + i / 16) + Vector3.up;
            if (Physics.Raycast(probe, Vector3.down, out RaycastHit hit, 3f, PhysicsLayers.Environment, QueryTriggerInteraction.Ignore)
                && hit.collider.GetComponent<SurfaceInkManager>() is SurfaceInkManager ink && ink.getSurfaceTeam(ink.UVFromHit(hit)) == player.Team) inked++;
        }
        Note($"{inked}/96 floor points 1-6m around it are our ink");
        if (inked == 0) Fail("didn't ink around itself");

        // One at a time: another throw replaces it. 30 HP.
        player.RefillInk();
        loadout.UseSub();
        yield return null;
        if (s != null) Fail("the first sprinkler survived a second throw");
        Sprinkler second = SubDevice.All.OfType<Sprinkler>().FirstOrDefault(d => d.OwnerId == player.OwnerId);
        yield return HoldUntil(Still, false, 3f, "throw", f => second == null || second.Landed);
        if (second != null) second.TakeDamage(29f, enemyTeam, 4242);
        if (second == null) Fail("broke before 30 damage");
        else second.TakeDamage(1f, enemyTeam, 4242);
        yield return null;
        if (second != null) Fail("survived 30 damage");

        // Someone else's: flies from their throw, snaps to their landing, sprays (visual only).
        SpawnRemote(FoeId, enemyTeam, station.TransformPoint(new Vector3(-9f, 0.05f, 8f)), "Foe");
        yield return Hold(Still, false, 0.1f, "remote");
        Vector3 from = station.TransformPoint(new Vector3(-6f, 2f, 6f)), at = station.TransformPoint(new Vector3(-3f, 0f, 6f));
        gm.Receive(NetMsg.SubSpawn, new SubData { ownerId = FoeId, subId = 9, team = enemyTeam, subType = (int)SubType.Sprinkler, position = from, velocity = new Vector3(4f, 3f, 0f), normal = Vector3.up }, FoeId);
        yield return Hold(Still, false, 0.15f, "remote");
        Sprinkler theirs = SubDevice.Find(FoeId, 9) as Sprinkler;
        if (theirs == null || theirs.Landed || theirs.IsOwnedLocally) Fail("their thrown sprinkler wasn't created in flight");
        gm.Receive(NetMsg.SubSpawn, new SubData { ownerId = FoeId, subId = 9, team = enemyTeam, subType = (int)SubType.Sprinkler, position = at, normal = Vector3.up, landed = true }, FoeId);
        yield return Hold(Still, false, 1f, "remote");
        if (theirs == null || !theirs.Landed || Vector3.Distance(theirs.transform.position, at) > 0.01f) Fail("their sprinkler didn't snap to the owner's landing");
        else if (theirs.ShotsFired == 0) Fail("their sprinkler isn't spraying");
        gm.Receive(NetMsg.SubDestroy, new SubData { ownerId = FoeId, subId = 9 }, FoeId);
        yield return Hold(Still, false, 0.1f, "remote");
        if (SubDevice.Find(FoeId, 9) != null) Fail("their destroy message didn't remove it");

        Despawn(FoeId);
        loadout.SetSub(SubType.Beacon);
        yield return Hold(Still, false, 0.5f, "settle");
        floor.FillRegion(new Rect(0, 0, 1, 1), 0);
        NetGameManager.SendOverride = null;
    }

    // Runs last: starting the match clears all ink.
    IEnumerator Hud()
    {
        MatchHUD hud = FindFirstObjectByType<MatchHUD>();
        if (hud == null) { Fail("no MatchHUD in the scene"); yield break; }
        NetGameManager gm = NetGameManager.Instance;
        const ulong RemoteId = 2002;
        int enemyTeam = player.Team == 1 ? 2 : 1;
        NetGameManager.SendOverride = (id, data, target) => { };
        MatchHUD.PlayerSlot[] own   = player.Team == 1 ? hud.AlphaSlots : hud.BetaSlots;
        MatchHUD.PlayerSlot[] enemy = player.Team == 1 ? hud.BetaSlots : hud.AlphaSlots;
        yield return Hold(Still, false, 0.1f, "roster");

        // Rosters: us in our team's first slot (highlighted), the enemy side empty.
        if (own[0].playerName.text != player.DisplayName || !own[0].localMark.enabled)
            Fail($"local player not shown in our first slot (\"{own[0].playerName.text}\", highlighted {own[0].localMark.enabled})");
        if (enemy.Any(s => s.playerName.text != "")) Fail("enemy slots aren't empty before anyone joins");

        // An enemy joins, dies, then leaves.
        Vector3 remotePos = station.TransformPoint(new Vector3(6f, 0.05f, 6f));
        gm.Receive(NetMsg.PlayerSpawn, new PlayerSpawnData { steamId = RemoteId, team = enemyTeam, position = remotePos, playerName = "Rival" }, RemoteId);
        yield return Hold(Still, false, 0.1f, "roster");
        if (enemy[0].playerName.text != "Rival" || enemy[0].initial.text != "R" || enemy[0].localMark.enabled)
            Fail($"joined enemy not shown correctly (\"{enemy[0].playerName.text}\"/\"{enemy[0].initial.text}\")");
        gm.Receive(NetMsg.PlayerState, new PlayerStateData { steamId = RemoteId, position = remotePos, moveDir = Vector2.zero, team = enemyTeam, dead = true }, RemoteId);
        yield return Hold(Still, false, 0.1f, "roster");
        if (!enemy[0].deadMark.activeSelf) Fail("dead enemy isn't marked");
        gm.Receive(NetMsg.PlayerDespawn, new PlayerSpawnData { steamId = RemoteId }, RemoteId);
        yield return Hold(Still, false, 0.1f, "roster");
        if (enemy[0].playerName.text != "") Fail("enemy slot not cleared after they left");

        // Timer: full duration before the match, counting down after it starts.
        string before = hud.TimerText;
        gm.SetMatchTimings(0.3f, 180f, 60f, 2.5f, 15f); // short countdown
        gm.StartGame();
        frames.Clear(); // starting a match respawns us at our spawn
        yield return Hold(Still, false, 1.5f, "timer");
        string after = hud.TimerText;
        Note($"timer {before} -> {after} after 1.2s");
        if (gm.Phase != MatchPhase.Playing) Fail("match didn't start");
        if (after == before) Fail($"timer isn't counting down ({before} -> {after})");

        // Turf bar: everything neutral after the reset, then our share grows as we paint.
        yield return Hold(Still, false, 1.2f, "turf");
        float ourShare() => player.Team == 1 ? hud.AlphaShare : hud.BetaShare;
        if (ourShare() > 0.0005f) Fail($"coverage not reset by the match start ({ourShare():P1})");
        for (int i = 0; i < 12; i++)
        {
            Vector3 p = station.TransformPoint(new Vector3(-9f + (i % 6) * 3.5f, 3f, -4f - (i / 6) * 3.5f));
            ProjectileManager.Fire(projectilePrefab, p, Vector3.down * 8f, 30, player.Team, true, authoritative: true, ownerId: player.OwnerId);
        }
        yield return Hold(Still, false, 2.5f, "turf");
        Note($"our coverage {ourShare():P2}, bar fill {(player.Team == 1 ? hud.ShownAlphaFill : 0f):F4}");
        if (ourShare() <= 0f) Fail("turf bar didn't pick up the painted floor");
        if (player.Team == 1 && hud.ShownAlphaFill <= 0f) Fail("alpha bar didn't move");

        gm.EndMatch();
        gm.SetMatchTimings(3f, 180f, 60f, 2.5f, 15f);
        NetGameManager.SendOverride = null;
    }

    // ── On-screen results ───────────────────────────────────────────────────

    void OnGUI()
    {
        GUILayout.BeginArea(new Rect(10, 10, 720, Screen.height - 20), GUI.skin.box);
        GUILayout.Label(running ? "Climb tests running..." : "Climb tests — F12: run all   F1–F9: teleport to station");
        scroll = GUILayout.BeginScrollView(scroll);
        foreach (Result r in results)
        {
            GUI.color = r.pass ? Color.green : new Color(1f, 0.45f, 0.45f);
            GUILayout.Label($"{(r.pass ? "PASS" : "FAIL")}  {r.name}");
            if (!string.IsNullOrEmpty(r.detail)) { GUI.color = Color.white; GUILayout.Label("      " + r.detail); }
        }
        GUI.color = Color.white;
        GUILayout.EndScrollView();
        GUILayout.EndArea();
    }
}

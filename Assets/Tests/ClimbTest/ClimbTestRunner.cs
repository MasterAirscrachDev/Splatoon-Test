using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.InputSystem.DualShock;
using UnityEngine.InputSystem.Controls;
using UnityEngine.EventSystems;
using static ClimbTestLayout;

// Runs scripted scenarios in ClimbTest.unity and logs "[ClimbTest] PASS/FAIL" plus a summary (also
// shown on screen). Fixed 1/75 s steps make runs repeatable. Station scenarios run side by side on
// several players (one per station at a time, see RunParallel); the Lobby ones, which share match,
// HUD and network state, run one at a time on the real local player. Watched from overhead.
// Each scenario is tagged with the systems it covers, so a run can be just those (see Choose).
// This file is the runner, the scenario list and the shared helpers; the scenarios themselves are in
// ClimbTestRunner.<Area>.cs by area.
// Manual use: F1–F9 teleport to stations 1–9, F11 toggles overhead/player view, F12 reruns everything.
public partial class ClimbTestRunner : MonoBehaviour
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

    const float DefaultDt = 1f / 75f;
    static float Dt = DefaultDt; // the run's fixed frame step: 1/75 unless the request asks for another (fps: N)
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
        public string[] tags;  // the systems it covers, for running just those (see Choose)
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
        // Tests run on default settings and save to their own file, never the player's.
        while (!GameSettings.Loaded) yield return null;
        GameSettings.FileName = "test_settings.cfg";
        GameSettings.ResetToDefaults();
        // ...and the default loadout, putting the player's saved choice back afterwards.
        int savedWeapon = PlayerPrefs.GetInt(PlayerLoadout.WeaponPref, 0), savedSub = PlayerPrefs.GetInt(PlayerLoadout.SubPref, 0), savedSpecial = PlayerPrefs.GetInt(PlayerLoadout.SpecialPref, 0);
        PlayerLoadout startLoadout = player.GetComponent<PlayerLoadout>();
        if (startLoadout != null)
        {
            startLoadout.SetMainWeapon(0);
            startLoadout.SetSub(SubType.Beacon);
            startLoadout.SetSpecial(SpecialType.BubbleShield);
        }
        List<Scenario> all = Scenarios().ToList();
        List<Scenario> chosen = Choose(all, out string selection);
        Time.captureDeltaTime = Dt;
        List<Scenario> stations = chosen.Where(s => s.station != Station.Lobby).ToList();
        if (stations.Count > 0) yield return RunParallel(stations);
        lane = mainLane;
        foreach (Scenario s in chosen.Where(s => s.station == Station.Lobby))
            yield return RunScenario(s);

        Time.captureDeltaTime = 0f;
        PlayerPrefs.SetInt(PlayerLoadout.WeaponPref, savedWeapon);
        PlayerPrefs.SetInt(PlayerLoadout.SubPref, savedSub);
        PlayerPrefs.SetInt(PlayerLoadout.SpecialPref, savedSpecial);
        player.ScriptedInput = null;
        NetGameManager.SendOverride = null;
        Application.logMessageReceived -= OnLog;

        int passed = results.Count(r => r.pass);
        WriteLog(LastFailedFile, results.Where(r => !r.pass).Select(r => r.name));
        string ran = (chosen.Count == all.Count ? $"all {all.Count} scenarios" : $"{selection}: {chosen.Count} of {all.Count} scenarios")
                   + (Dt != DefaultDt ? $" at {1f / Dt:0} fps" : "");
        StringBuilder sb = new StringBuilder($"[ClimbTest] SUMMARY {passed}/{results.Count} passed ({ran})\n");
        foreach (Result r in results) sb.AppendLine($"  {(r.pass ? "PASS" : "FAIL")}  {r.name}");
        Debug.Log(sb.ToString());
        running = false;

#if UNITY_EDITOR
        if (exitPlayModeWhenDone) UnityEditor.EditorApplication.isPlaying = false;
#endif
    }

    // ── Choosing what to run ────────────────────────────────────────────────

    // Everything, unless the run script left a request in Logs/ClimbTest/run_request.txt (read once,
    // then deleted, so a later manual run is a full one) or the inspector's filter is set. Request
    // lines, any mix, a scenario running if it matches any of them:
    //   tags: Weapons, Blast    covering any of these systems (Climb, Swim, Camera, Input, UI, Map,
    //                           Projectiles, Weapons, Blast, Subs, Specials, Combat, Match, Net, Ink,
    //                           Roller, Facing, FrameRate)
    //   filter: Blaster         whose name contains this (commas for several)
    //   failed                  whatever failed last run (last_failed.txt)
    //   fps: 144                the frame rate to step at (default 75), to catch frame-rate dependence;
    //                           on its own, everything runs at it
    const string RequestFile = "run_request.txt", LastFailedFile = "last_failed.txt";

    static string LogPath(string file) => System.IO.Path.Combine(Application.dataPath, "..", "Logs", "ClimbTest", file);

    static void WriteLog(string file, IEnumerable<string> lines)
    {
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(LogPath(file)));
        System.IO.File.WriteAllLines(LogPath(file), lines);
    }

    List<Scenario> Choose(List<Scenario> all, out string selection)
    {
        var lines = new List<string>();
        if (System.IO.File.Exists(LogPath(RequestFile)))
        {
            lines.AddRange(System.IO.File.ReadAllLines(LogPath(RequestFile)));
            System.IO.File.Delete(LogPath(RequestFile));
        }
        else if (!string.IsNullOrEmpty(filter)) lines.Add("filter: " + filter);
        lines = lines.Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        Dt = DefaultDt;
        string fpsLine = lines.FirstOrDefault(l => l.StartsWith("fps:", StringComparison.OrdinalIgnoreCase));
        if (fpsLine != null)
        {
            lines.Remove(fpsLine);
            if (float.TryParse(fpsLine.Substring(4).Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float fps) && fps >= 10f && fps <= 1000f)
                Dt = 1f / fps;
            else results.Add(new Result { name = $"Run request: bad frame rate '{fpsLine}'", pass = false });
        }
        selection = string.Join("; ", lines);
        if (lines.Count == 0) return all;

        List<string> List(string prefix) => lines.Where(l => l.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .SelectMany(l => l.Substring(prefix.Length).Split(',')).Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
        List<string> tags = List("tags:"), names = List("filter:");
        var failed = new HashSet<string>();
        if (lines.Any(l => l.Equals("failed", StringComparison.OrdinalIgnoreCase)) && System.IO.File.Exists(LogPath(LastFailedFile)))
            failed.UnionWith(System.IO.File.ReadAllLines(LogPath(LastFailedFile)));

        var known = new HashSet<string>(all.SelectMany(s => s.tags), StringComparer.OrdinalIgnoreCase);
        foreach (string t in tags.Where(t => !known.Contains(t)))
            results.Add(new Result { name = $"Run request: unknown tag '{t}' (known: {string.Join(", ", known.OrderBy(k => k))})", pass = false });

        return all.Where(s => s.tags.Any(t => tags.Contains(t, StringComparer.OrdinalIgnoreCase))
                           || names.Any(n => s.name.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0)
                           || failed.Contains(s.name)).ToList();
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
        // Whatever ran before may have left us dead or in a respawn's swim hold: wait it out, so
        // a scenario starts the same however the run was chosen.
        for (float waited = 0f; (player.IsDead || player.IsRespawning) && waited < 8f; waited += Time.deltaTime) yield return null;
        Teleport(s.station, s.spawn ?? "Spawn");
        for (float waited = 0f; !player.KidFormReady && waited < 2f; waited += Time.deltaTime) yield return null; // grown back from swim form
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
        string safe = new string(name.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
        string file = $"{index:00}_{(safe.Length > 80 ? safe.Substring(0, 80) : safe)}.csv"; // well inside Windows' path limit
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
                if (f.phase != frames[i - 1].phase && f.phase.StartsWith("placed")) continue; // the scenario moved us (PlaceAt)
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
        Scenario S(string name, Station st, Func<IEnumerator> body, string tags, string spawn = null) =>
            new Scenario { name = name, station = st, body = body, spawn = spawn, tags = tags.Split(',') };

        yield return S("Controls: every action bound for keyboard/mouse and gamepad; the right stick turns at a rate (same at any frame rate, faster at the edge); Recenter levels the view; B closes menus", Station.Lobby, Controls, "Input");
        yield return S("Control icons: each action shows its key/button art for keyboard/mouse, Xbox, PlayStation and Switch (text where there's no art); the last device used picks them, mouse movement alone doesn't", Station.Lobby, ControlIcons, "Input,UI");
        yield return S("Cursor: a ring placed directly by the mouse (or gyro, with Y to recentre), moved at a rate by the stick in the map, A clicks; a gamepad picks menu buttons instead", Station.Lobby, CursorControl, "Input,UI");
        yield return S("Gyro (Steam Input): its calls resolve; it moves the view on a gamepad (the stick then only turns) and the cursor; Steam's pad types pick the icons", Station.Lobby, Gyro, "Input");
        yield return S("Map on a gamepad: the west button toggles it (B closes it too); the d-pad Super Jumps to teammates, down to base, shown on their entries", Station.Lobby, MapPadButtons, "Input,Map");
        yield return S("Pause menu: Esc/Select open it; settings scale mouse, stick, gyro X/Y, gyro off, saved; Y recentres under gyro; RT clicks", Station.Lobby, PauseAndSettings, "Input,UI");
        yield return S("Frame rate: a jump peaks at the same height at 30, 75 and 144 fps; standing stays grounded every frame even at 1000 fps", Station.Lobby, FrameRateIndependence, "FrameRate,Swim");
        yield return S("Lobby: switching form glides the camera instead of snapping", Station.Lobby, CameraFormSwitch, "Camera,Swim");
        yield return S("Lobby: pooled projectiles splat where they land and are reused", Station.Lobby, ProjectilePooling, "Projectiles,Ink");
        yield return S("Projectiles: speed/gravity curves retime a path without changing it (Ballistics.Faster too, with curves); fast shots can't skip hitboxes; damage falls off along a curve over flight time", Station.Lobby, ProjectileBallistics, "Projectiles");
        yield return S("FlatWall: climb up and over the top", Station.FlatWall, ClimbUpAndOver, "Climb");
        yield return S("FlatWall: no input on the wall slides slowly without letting go", Station.FlatWall, HoldStillOnWall, "Climb");
        yield return S("FlatWall: climb down to the floor and swim away", Station.FlatWall, ClimbDownAndAway, "Climb,Swim");
        yield return S("FlatWall: jump while climbing up boosts and stays on the wall", Station.FlatWall, JumpBoost, "Climb");
        yield return S("FlatWall: jump while descending ejects from the wall", Station.FlatWall, JumpEject, "Climb");
        yield return S("FlatWall: leaving swim form on the wall drops cleanly to the floor", Station.FlatWall, LeaveSwimOnWall, "Climb");
        yield return S("Ledge: swimming off a ledge falls past the inked wall below", Station.Ledge, LedgeWalkOff, "Climb,Swim");
        yield return S("Pillar: strafing around an outer corner keeps climbing", Station.Pillar, OuterCorner, "Climb");
        yield return S("InnerCorner: strafing into an inner corner transfers to the side wall", Station.InnerCorner, InnerCorner, "Climb");
        yield return S("NeutralWall: an unpainted wall can't be climbed", Station.NeutralWall, CannotClimb, "Climb");
        yield return S("EnemyWall: an enemy-ink wall can't be climbed", Station.EnemyWall, CannotClimb, "Climb");
        yield return S("PartialWall: reaching the top of own ink pops off, then re-grabs after 0.2s", Station.PartialWall, PartialWallPopOff, "Climb");
        yield return S("PartialWall: holding up bobs at the ink edge without climbing past it", Station.PartialWall, PartialWallBob, "Climb");
        yield return S("Ramp30: swims up a walkable slope without climb mode or jitter", Station.Ramp30, ShallowRampUp, "Climb,Swim");
        yield return S("Ramp30: swims down a walkable slope without bouncing", Station.Ramp30, ShallowRampDown, "Climb,Swim", "SpawnTop");
        yield return S("Ramp60: a steep slope is climbed", Station.Ramp60, SteepRamp, "Climb,Swim");
        yield return S("Combat: replays are visual-only, hits route to the victim, damage kills and respawns", Station.Lobby, Combat, "Combat,Projectiles,Net");
        yield return S("Damage ink: enemy hits put their ink over our screen's edges, further the more we're hurt, gone once healed; a lethal hit floods it, then it drains", Station.Lobby, DamageInk, "Combat,UI");
        yield return S("Death: the camera circles the spot while a popup says who and with what; back at spawn 4s later, held in swim form for 1s; falling out of bounds too", Station.Lobby, Death, "Combat,Camera,UI");
        yield return S("Kills: the victim's client tells everyone; the feed says who did what to whom (or that they fell), only the latest five; the killer gets a crossed-out marker; the victim bursts into the killer's ink", Station.Lobby, KillsAndFeed, "Kills,Combat,UI,Net");
        yield return S("HostMenu: percentages toggle, special fills, team editor moves players between teams and the bench", Station.Lobby, HostMenuTeams, "Match,UI");
        yield return S("Map: holding opens it, lists our team with lines to markers, picking a teammate requests a Super Jump", Station.Lobby, MapTeam, "Map,UI");
        yield return S("SuperJump: locked charge in swim form, high arc onto the teammate, unlocks on landing (swim kept only if held)", Station.Lobby, SuperJump, "Map,Swim");
        yield return S("SuperJump: the landing spot locks on click, only our death cancels, spawn is always a target", Station.Lobby, SuperJumpLocking, "Map");
        yield return S("Spawn barrier: a sphere curving out of a small pad, hidden until the other team nears it or attacks it (then shown to both teams); keeps them out (walking, swimming, or pushed out), stops their shots, breaks their subs; nothing of theirs hurts us in our own spawn", Station.Lobby, SpawnBarrier, "Map,Combat,Spawn");
        yield return S("Sub: a beacon costs 70% ink, shows on our map only, and a Super Jump onto it breaks it", Station.Lobby, SubBeacon, "Subs,Map");
        yield return S("Beacon: 35 health from enemy fire only, expires, remote hits go to the owner", Station.Lobby, BeaconHealth, "Subs,Combat,Net");
        yield return S("Special: charges from new turf and damage dealt; bubble shield refills ink and blocks damage for 5s", Station.Lobby, SpecialShield, "Specials,Combat");
        yield return S("Loadout HUD: gauges follow ink and special charge, READY when full", Station.Lobby, LoadoutGauges, "UI,Subs,Specials");
        yield return S("Special: half lost on death; charged state is synced and shown on the player bar", Station.Lobby, SpecialDeathAndSync, "Specials,Net");
        yield return S("Lobby: swimming is fastest in our ink, slower on bare ground, slowest in enemy ink", Station.Lobby, SwimSpeeds, "Swim,Ink");
        yield return S("Lobby: swimming raises a ring of ink around the squid and spraying side wakes that grow with speed; nothing while still", Station.Lobby, SwimWakeTrail, "Swim");
        yield return S("LobbyMenu: hidden in dev mode, lists lobbies as rows, a failed join reports and refreshes", Station.Lobby, LobbyMenuList, "Match,UI");
        yield return S("Colours: a random pair from the set each load, the host's pick reaches everyone and recolours ink, players and HUD", Station.Lobby, TeamColourPairs, "Match,Ink,Net");
        yield return S("Match: countdown locks and resets everyone, final minute hides the bar (no banner), results fill to a suspense point then the real split", Station.Lobby, MatchFlow, "Match");
        yield return S("Match: a mid-match joiner spectates the rest of the round, then gets their team back", Station.Lobby, LateJoin, "Match,Net");
        yield return S("Spectating: no player of our own means the overhead view; click a player at the top to watch them, the minimap to see everything again", Station.Lobby, SpectatorView, "Match,Camera,Teams");
        yield return S("Weapon: holding fire also drops 12-18 ink at our feet every 0.4s (shooters and the Blaster)", Station.Lobby, FeetInk, "Weapons,Ink");
        yield return S("Blaster: one straight shot that explodes at its range (or on a wall), inks around it; 125 direct, blast falls off with distance; replays are visual-only", Station.Lobby, BlasterWeapon, "Weapons,Projectiles,Blast,Combat,Net");
        yield return S("Lobby flow: the kiosk opens the online menu as we walk up and closes it as we leave; the host's map switch sends everyone and loads it; no matches on the lobby map; a LoadScene loads it here; a scene's spare GameCore is destroyed", Station.Lobby, LobbyFlow, "Match,Net");
        yield return S("Net codec: every message type survives the trip byte for byte, far smaller than BinaryFormatter; unknown, cut-short, oversized or padded messages and unlisted types are refused", Station.Lobby, NetCodecMessages, "Net");
        yield return S("Ink seams: a splat near where two surfaces meet carries on across the join (same centre and size), not past its circle; far from it, or switched off, it stops at its own edge", Station.Lobby, SeamBridging, "Ink");
        yield return S("Ink on the GPU: a frame's splats paint in grouped dispatches (one per cluster, readbacks likewise), in the order they came", Station.Lobby, SplatGrouping, "Ink");
        yield return S("Teams: the map decides how many (up to 4): players dealt round them, four ink channels owned and counted, Gamma's spawn, HUD rows and editor columns only while the map has them, the top team wins", Station.Lobby, MoreTeams, "Teams,Match,Ink");
        yield return S("Ink sync: our splats go out batched (one message per tick, up to 64 each); a received batch paints every splat", Station.Lobby, SplatBatching, "Ink,Net");
        yield return S("Facing: the body turns to the way we move; firing (or a sub) turns it to the view at once, and the gun aims there; remotes see our facing", Station.Lobby, BodyFacing, "Facing,Weapons,Net");
        yield return S("Swim exit: a sub pressed while coming out of swim form goes as soon as the kid form is up (beacon, sprinkler, curling bomb)", Station.Lobby, SubOutOfSwim, "Subs,Swim");
        yield return S("Swim exit: holding attack while leaving swim form fires (or flicks) at once", Station.Lobby, AttackOutOfSwim, "Weapons,Swim,Roller");
        yield return S("Roller: a wide ground flick (into the rolling position) and a long air flick (frame on its side), aimed with the view, one hit per flick; held, it rolls a strip at 0.8x building to 1.2x speed facing the way we move, costs ink by the metre, runs enemies over (100); a jump lifts it; remotes see it down", Station.Lobby, RollerWeapon, "Weapons,Roller,Combat,Net,Facing");
        yield return S("Weapons: Inkshot fires slower, further and tighter than Airspray SE", Station.Lobby, WeaponVariety, "Weapons,Projectiles");
        yield return S("Weapons: prefabs swapped in our hands, synced to remote copies; jumping doesn't carry into shots", Station.Lobby, WeaponPrefabs, "Weapons,Net");
        yield return S("LoadoutMenu: picks the weapon and sub, applies and remembers them, can't open mid-match", Station.Lobby, LoadoutMenuPicks, "Weapons,Subs,UI");
        yield return S("InkStrike: aimed on the map (cancel keeps the charge), marked, then inks the whole column down to the ground; lethal core, non-fatal edge", Station.Lobby, InkStrikeSpecial, "Specials,Map,Combat");
        yield return S("Sprinkler: thrown, sticks, sprays both ways and winds down; one at a time; 30 HP; remote copies follow the owner", Station.Lobby, SprinklerSub, "Subs,Projectiles,Net");
        yield return S("Curling bomb on slopes: climbs a ramp slowing down (then rolls back), speeds up coming down, hugs and tilts with the surface", Station.Lobby, CurlingBombSlopes, "Subs");
        yield return S("Curling bomb: slides inking a stripe, clips enemies (15) and carries on, bounces off walls, explodes on its fuse (45 in range); holding shortens the fuse", Station.Lobby, CurlingBombSub, "Subs,Blast,Combat");
        yield return S("HUD: rosters, death marks, match timer and turf bar track the game", Station.Lobby, Hud, "UI,Match");
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

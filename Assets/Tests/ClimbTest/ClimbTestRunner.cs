using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;
using UnityEngine.InputSystem;
using static ClimbTestLayout;

// Runs scripted movement scenarios in ClimbTest.unity and logs "[ClimbTest] PASS/FAIL" plus a
// summary (also shown on screen). Fixed 1/60 s steps make runs repeatable.
// Manual use: F1–F9 teleport to stations 1–9, F12 reruns everything.
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

    const float Dt = 1f / 60f;
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

    PlayerController player;
    Transform station;
    readonly List<Frame> frames = new List<Frame>();
    readonly List<string> failures = new List<string>();
    readonly List<string> notes = new List<string>();
    readonly List<Result> results = new List<Result>();
    int errorLogs;
    // Errors logged during scene startup, reported as their own failing result.
    int startupErrors;
    string firstStartupError;
    bool running;
    Vector2 scroll;

    void Awake() => Application.logMessageReceived += OnStartupLog;
    void OnDestroy() => Application.logMessageReceived -= OnStartupLog;

    void OnStartupLog(string message, string stack, LogType type)
    {
        if (type != LogType.Error && type != LogType.Exception && type != LogType.Assert) return;
        if (startupErrors++ == 0) firstStartupError = message;
    }

    // ── Lifecycle ───────────────────────────────────────────────────────────

    IEnumerator Start()
    {
        while ((player = NetGameManager.LocalPlayer) == null) yield return null;
        for (int i = 0; i < 10; i++) yield return null; // let TestInkFill and its readbacks land
        if (runOnStart) yield return RunAll();
    }

    void Update()
    {
        Keyboard kb = Keyboard.current;
        if (kb == null || running || player == null) return;
        if (kb.f12Key.wasPressedThisFrame) { StartCoroutine(RunAll()); return; }
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

        foreach (Scenario s in Scenarios())
        {
            if (!string.IsNullOrEmpty(filter) && s.name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
            yield return RunScenario(s);
        }

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

    IEnumerator RunScenario(Scenario s)
    {
        frames.Clear(); failures.Clear(); notes.Clear(); errorLogs = 0;
        player.ScriptedInput = new ScriptedPlayerInput();
        Teleport(s.station, s.spawn ?? "Spawn");
        for (int i = 0; i < 15; i++) yield return null; // settle on the floor, standing
        frames.Clear();

        yield return s.body();

        CheckInvariants();
        if (errorLogs > 0) Fail($"{errorLogs} error/exception log(s) during the scenario");
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
        if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert) errorLogs++;
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
        yield return S("HostMenu: percentages toggle, team editor moves players between teams and the bench", Station.Lobby, HostMenuTeams);
        yield return S("Map: holding opens it, lists our team with lines to markers, picking a teammate requests a Super Jump", Station.Lobby, MapTeam);
        yield return S("SuperJump: locked charge in swim form, high arc onto the teammate, unlocks on landing (swim kept only if held)", Station.Lobby, SuperJump);
        yield return S("SuperJump: the landing spot locks on click, only our death cancels, spawn is always a target", Station.Lobby, SuperJumpLocking);
        yield return S("Sub: a beacon costs 70% ink, shows on our map only, and a Super Jump onto it breaks it", Station.Lobby, SubBeacon);
        yield return S("Beacon: 35 health from enemy fire only, expires, remote hits go to the owner", Station.Lobby, BeaconHealth);
        yield return S("Special: charges from new turf and damage dealt; bubble shield refills ink and blocks damage for 5s", Station.Lobby, SpecialShield);
        yield return S("Loadout HUD: gauges follow ink and special charge, READY when full", Station.Lobby, LoadoutGauges);
        yield return S("Special: half lost on death; charged state is synced and shown on the player bar", Station.Lobby, SpecialDeathAndSync);
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
        int CountAll()    => FindObjectsByType<ProjectileSystem>(FindObjectsInactive.Include, FindObjectsSortMode.None).Length;
        int CountActive() => FindObjectsByType<ProjectileSystem>(FindObjectsInactive.Exclude, FindObjectsSortMode.None).Length;

        // Three volleys of 6 team-2 shots straight down onto the floor, half with hidden visuals.
        int before = CountAll(), afterFirst = 0;
        var targets = new List<Vector3>();
        for (int volley = 0; volley < 3; volley++)
        {
            for (int i = 0; i < 6; i++)
            {
                Vector3 p = station.TransformPoint(new Vector3(-6f + i * 2.4f, 3f, 4f + volley * 2.5f));
                targets.Add(new Vector3(p.x, station.position.y, p.z));
                ProjectilePool.Get(projectilePrefab, p, Quaternion.LookRotation(Vector3.down))
                              .Setup(Vector3.down * 8f, 10, 2, visible: i % 2 == 0);
            }
            yield return Hold(Still, false, 0.6f, "volley");
            if (volley == 0) afterFirst = CountAll() - before;
        }
        yield return Hold(Still, false, 0.3f, "settle");

        int created = CountAll() - before, stillActive = CountActive();
        Note($"{targets.Count} shots used {created} instances");
        if (afterFirst == 0) Fail("no projectile instances were created");
        if (created > afterFirst) Fail($"pool didn't reuse instances ({afterFirst} after the first volley, {created} after three)");
        if (stillActive > 0) Fail($"{stillActive} projectile(s) never returned to the pool");

        int painted = 0;
        foreach (Vector3 t in targets)
            if (Physics.Raycast(t + Vector3.up, Vector3.down, out RaycastHit hit, 2f, PhysicsLayers.Environment, QueryTriggerInteraction.Ignore)
                && hit.collider.TryGetComponent(out SurfaceInkManager ink) && ink.getSurfaceTeam(hit.textureCoord) == 2)
                painted++;
        if (painted < targets.Count) Fail($"only {painted}/{targets.Count} shots painted where they landed");
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
        int activeBefore = FindObjectsByType<ProjectileSystem>(FindObjectsInactive.Exclude, FindObjectsSortMode.None).Length;
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
        int replays = FindObjectsByType<ProjectileSystem>(FindObjectsInactive.Exclude, FindObjectsSortMode.None).Length - activeBefore;
        yield return Hold(Still, false, 0.8f, "replay");

        if (replays != 2) Fail($"expected 2 replayed projectiles, saw {replays}");
        if (player.Hitbox.Health < fullHealth) Fail($"a replayed shot damaged us ({player.Hitbox.Health}/{fullHealth})");
        if (TeamAt(floorSpot) != floorTeam) Fail("a replayed shot painted the floor (only the shooter's Splat message should)");
        if (sent.Any(m => m.id == NetMsg.Damage || m.id == NetMsg.Splat)) Fail("replayed shots sent Damage/Splat messages");

        // 2. Our shots hitting the remote copy are forwarded to that player, not applied locally.
        sent.Clear();
        for (int i = 0; i < 3; i++)
        {
            ProjectilePool.Get(projectilePrefab, remote.BodyCenter + Vector3.up * 3f, Quaternion.LookRotation(Vector3.down))
                          .Setup(Vector3.down * 8f, 10, player.Team, true, authoritative: true, ownerId: player.OwnerId);
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
        ProjectilePool.Get(projectilePrefab, above, Quaternion.LookRotation(Vector3.down))
                      .Setup(Vector3.down * 10f, 10, team, true, authoritative: true, ownerId: ownerId);

    IEnumerator SubBeacon()
    {
        NetGameManager gm = NetGameManager.Instance;
        PlayerLoadout loadout = player.GetComponent<PlayerLoadout>();
        MapScreen map = FindFirstObjectByType<MapScreen>();
        if (loadout == null || map == null) { Fail("player has no PlayerLoadout (or no MapScreen)"); yield break; }
        CaptureSends();
        Beacon.RemoveAll();
        const ulong FoeId = 3011;
        int enemyTeam = player.Team == 1 ? 2 : 1;

        // Costs the sub's share of ink; with less than that, nothing happens.
        player.RefillInk();
        yield return Hold(Still, false, 0.2f, "stand");
        if (!loadout.UseSub()) { Fail("couldn't place a beacon with a full tank"); NetGameManager.SendOverride = null; yield break; }
        Beacon beacon = Beacon.All.First(b => b.OwnerId == player.OwnerId);
        Vector3 beaconPos = beacon.transform.position;
        float ahead = Vector3.Dot(beaconPos - player.transform.position, player.transform.forward);
        Note($"ink after placing {player.InkLevel:F2}, beacon {ahead:F1}m ahead");
        if (Mathf.Abs(player.InkLevel - 0.3f) > 0.01f) Fail($"ink went to {player.InkLevel:F2}, not 0.30");
        if (ahead < 0.5f) Fail("beacon isn't in front of the player");
        if (!sent.Any(m => m.id == NetMsg.BeaconSpawn)) Fail("beacon wasn't broadcast");
        if (loadout.UseSub() || Beacon.All.Count != 1) Fail("placed a beacon with only 30% ink");

        // An enemy beacon arrives from its owner; only ours shows on our map.
        SpawnRemote(FoeId, enemyTeam, station.TransformPoint(new Vector3(-9f, 0.05f, 8f)), "Foe");
        yield return Hold(Still, false, 0.1f, "stand");
        gm.Receive(NetMsg.BeaconSpawn, new BeaconData { ownerId = FoeId, beaconId = 1, team = enemyTeam, position = station.TransformPoint(new Vector3(-8f, 0f, 8f)) }, FoeId);
        yield return Hold(Still, false, 0.1f, "stand");
        if (Beacon.Find(FoeId, 1) == null) Fail("enemy beacon wasn't created from its message");

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
            if (!sent.Any(m => m.id == NetMsg.BeaconDestroy)) Fail("the break wasn't broadcast");
        }

        Despawn(FoeId);
        yield return Hold(Still, false, 0.1f, "stand");
        if (Beacon.Find(FoeId, 1) != null) Fail("enemy beacon outlived its owner leaving");
        NetGameManager.SendOverride = null;
    }

    IEnumerator BeaconHealth()
    {
        NetGameManager gm = NetGameManager.Instance;
        PlayerLoadout loadout = player.GetComponent<PlayerLoadout>();
        if (loadout == null) { Fail("player has no PlayerLoadout"); yield break; }
        CaptureSends();
        Beacon.RemoveAll();
        const ulong FoeId = 3012;
        int enemyTeam = player.Team == 1 ? 2 : 1;

        player.RefillInk();
        loadout.UseSub();
        Beacon beacon = Beacon.All.FirstOrDefault(b => b.OwnerId == player.OwnerId);
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
        if (!sent.Any(m => m.id == NetMsg.BeaconDestroy)) Fail("breaking wasn't broadcast");

        // Lifetime: one left alone expires.
        player.RefillInk();
        loadout.UseSub();
        Beacon timed = Beacon.All.FirstOrDefault(b => b.OwnerId == player.OwnerId);
        if (timed != null) timed.ExpireIn(0.3f);
        yield return Hold(Still, false, 0.6f, "expire");
        if (timed != null) Fail("didn't expire");

        // Someone else's: our hit goes to its owner (who decides), and charges our special.
        SpawnRemote(FoeId, enemyTeam, station.TransformPoint(new Vector3(-9f, 0.05f, 8f)), "Foe");
        yield return Hold(Still, false, 0.1f, "remote");
        gm.Receive(NetMsg.BeaconSpawn, new BeaconData { ownerId = FoeId, beaconId = 4, team = enemyTeam, position = station.TransformPoint(new Vector3(6f, 0f, 6f)) }, FoeId);
        yield return Hold(Still, false, 0.1f, "remote");
        Beacon theirs = Beacon.Find(FoeId, 4);
        if (theirs == null) { Fail("their beacon wasn't created"); }
        else
        {
            sent.Clear();
            float points = loadout.SpecialPoints;
            Shoot(theirs.transform.position + Vector3.up * 3f, player.Team, player.OwnerId);
            yield return Hold(Still, false, 0.6f, "remote");
            var hit = sent.FirstOrDefault(m => m.id == NetMsg.BeaconDamage);
            Note($"special +{loadout.SpecialPoints - points:F0}p for the hit");
            if (hit.data == null || hit.to != FoeId) Fail("hit on their beacon wasn't sent to its owner");
            if (theirs == null || theirs.Health != 35f) Fail("our copy applied the damage itself");
            if (loadout.SpecialPoints - points < 1f) Fail("hitting their beacon didn't charge the special");
            gm.Receive(NetMsg.BeaconDestroy, new BeaconData { ownerId = FoeId, beaconId = 4 }, FoeId);
            yield return Hold(Still, false, 0.1f, "remote");
            if (Beacon.Find(FoeId, 4) != null) Fail("their break message didn't remove it");
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
        float ours = loadout.SpecialPoints - before;
        foe.Hitbox.TakeDamage(30f, player.Team, 99999);
        float theirs = loadout.SpecialPoints - before - ours;
        Note($"special +{ours:F0}p for our 30 damage, +{theirs:F0}p for someone else's");
        if (Mathf.Abs(ours - 30f) > 0.01f || theirs != 0f) Fail("damage dealt didn't charge 1p per damage (ours only)");

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
        gm.ResetMap();
        yield return Hold(Still, false, 1.2f, "timer");
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
            ProjectilePool.Get(projectilePrefab, p, Quaternion.LookRotation(Vector3.down))
                          .Setup(Vector3.down * 8f, 30, player.Team, true, authoritative: true, ownerId: player.OwnerId);
        }
        yield return Hold(Still, false, 2.5f, "turf");
        Note($"our coverage {ourShare():P2}, bar fill {(player.Team == 1 ? hud.ShownAlphaFill : 0f):F4}");
        if (ourShare() <= 0f) Fail("turf bar didn't pick up the painted floor");
        if (player.Team == 1 && hud.ShownAlphaFill <= 0f) Fail("alpha bar didn't move");

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

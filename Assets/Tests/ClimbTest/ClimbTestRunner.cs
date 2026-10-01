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
        public Vector3 normal;      // climb normal (world)
        public Quaternion squidRot; // squid visual rotation
        public int surfaceTeam;     // ink team directly below
        public float velY, speed;   // PlayerController's vertical velocity and current move speed
        public CollisionFlags flags;
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
            normal = player.ClimbNormal,
            squidRot = squid != null ? squid.rotation : Quaternion.identity,
            surfaceTeam = player.SurfaceTeam,
            velY = player.VerticalVelocity,
            speed = player.CurrentSpeed,
            flags = player.LastCollisionFlags,
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
                if (f.dead != frames[i - 1].dead) continue; // death/respawn teleports
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
            // Loose bounds so they don't encode the tuned climbPopTopLift.
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

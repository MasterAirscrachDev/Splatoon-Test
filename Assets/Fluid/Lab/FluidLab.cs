using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.InputSystem;
using Random = UnityEngine.Random;

// The fluid lab (Assets/Fluid/Lab/FluidLab.unity): every main weapon's attacks mimicked side by side,
// firing ahead (+z) on a loop over an inkable floor and back wall, to tune the ink's look
// (InkFluidSettings) against the whole set, apart from the game. The mimics are built from the real
// weapon prefabs (the player's loadout; their settings read directly, as each shot fires, so edits
// to the prefabs show at once): fire rates, speeds, counts, spreads, splat sizes, looks, flick paces
// and ballistics. Their shots are the game's own (ProjectileManager): they fly, paint, splatter and
// explode, and are drawn (InkFluid), exactly as in a match. Four teams (one of the game's colour
// sets; teams 1-4 across the row).
// Past the row's ends: a Sprinkler (a model, spinning and spraying as the sub does, winding down over
// its life) and an Inkstrike (the real one).
// Keys: P pauses; [ and ] slow time down and speed it up (T toggles); 1-9 toggle each weapon, F2 the
// sprinkler, F3 the inkstrike, 0 turns them all on; C changes the colour set; X clears the ink.
// The camera flies (FlyCamera) at its own speed whatever the time scale.
public class FluidLab : MonoBehaviour
{
    class Mimic
    {
        public string name;
        public Vector3 at;
        public int team;
        public float rate, next;
        public bool on = true;
        public int fired;
        public Action<Mimic> fire;
        public string key;                // its toggle (null: its number in the row)
        public Action<Mimic, float> tick; // runs every frame while on, instead of firing on `rate`
        public Action<bool> show;         // its model shown (on) or hidden (off)
    }

    [SerializeField] Weapon[] weapons;               // the prefabs mimicked (the player's loadout, in its order)
    [SerializeField] int colourSet = 1;              // NetGameManager.DefaultColourSets()
    [Header("Sub and special")]
    [SerializeField] GameObject sprinklerPrefab;
    [SerializeField] InkStrike inkStrikePrefab;
    [SerializeField] Vector3 sprinklerAt = new Vector3(18.5f, 0f, 14f);
    [SerializeField] Vector3 inkStrikeAt = new Vector3(-18.5f, 0f, 14f);
    [SerializeField] float inkStrikeEvery = 6f;      // seconds
    [Header("Time")]
    [SerializeField] bool paused;
    [SerializeField, Range(0.02f, 1f)] float timeScale = 1f;        // slow motion ([ and ], T toggles): Time.timeScale; the camera keeps its speed

    public static FluidLab Instance { get; private set; }
    public bool Paused { get => paused; set => paused = value; } // freezes everything (P), to look at one moment
    public float TimeScale { get => timeScale; set => timeScale = Mathf.Clamp(value, SlowSteps[0], 1f); }
    static readonly float[] SlowSteps = { 0.02f, 0.05f, 0.1f, 0.25f, 0.5f, 1f };
    float lastSlow = 0.25f;
    public int ColourSet { get => colourSet; set => colourSet = value; }

    readonly List<Mimic> mimics = new List<Mimic>();
    readonly List<(Renderer renderer, Mimic mimic)> tints = new List<(Renderer, Mimic)>(); // models' team-coloured parts
    SurfaceInkManager[] surfaces = new SurfaceInkManager[0];
    int appliedSet = -1;
    float fps = 60f;

    const float AimRaise = 5f;     // the game aims this far above the view (level here)

    public Color TeamColour(int team)
    {
        NetGameManager.TeamColours[] sets = NetGameManager.DefaultColourSets();
        NetGameManager.TeamColours c = sets[((colourSet % sets.Length) + sets.Length) % sets.Length];
        return team switch { 2 => c.beta, 3 => c.gamma, 4 => c.delta, _ => c.alpha };
    }

    // A weapon's own setting (its serialized fields aren't public).
    static T Field<T>(object o, string name)
    {
        for (Type t = o.GetType(); t != null; t = t.BaseType)
        {
            FieldInfo f = t.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (f != null) return (T)f.GetValue(o);
        }
        throw new MissingFieldException(o.GetType().Name, name);
    }

    void Awake()
    {
        Instance = this;
        InkFluid.ColourOverride = TeamColour; // no game manager here
        foreach (Weapon w in weapons)
        {
            if (w == null) continue;
            switch (w)
            {
                case WeaponShooter s: AddShooter(s); break;
                case Blaster b: AddBlaster(b); break;
                case Roller r: AddRoller(r); break;
                case Brush b: AddBrush(b); break;
                case Bucket b: AddBucket(b); break;
            }
        }
        for (int i = 0; i < mimics.Count; i++)
        {
            mimics[i].at = new Vector3((i - (mimics.Count - 1) * 0.5f) * 3.4f, 1.1f, 0f);
            mimics[i].team = i % 4 + 1;
            mimics[i].next = i * 0.07f;
        }
        AddSprinkler();
        AddInkStrike();
    }

    void OnDestroy()
    {
        Time.timeScale = 1f;
        InkFluid.ColourOverride = null;
    }

    void Start() => surfaces = FindObjectsByType<SurfaceInkManager>(FindObjectsSortMode.None);

    // A weapon's mimic, firing at its rate (read as it fires, like everything else).
    void Add(Weapon w, string rate, Action<Mimic> fire) =>
        mimics.Add(new Mimic
        {
            name = w.DisplayName, rate = Field<float>(w, rate),
            fire = m => { m.rate = Field<float>(w, rate); fire(m); },
        });

    // A real shot from a mimic, by angles from straight ahead (+z).
    static void Fire(Mimic m, float yaw, float pitch, float speed, int splat, bool visible = true, Ballistics ballistics = null,
                     ShotLook look = null, bool splashes = true, float range = 0f, InkBlast blast = null,
                     float trailSpacing = 0f, int trailSize = 0) =>
        ProjectileManager.Fire(m.at, Quaternion.Euler(-pitch, yaw, 0f) * Vector3.forward * speed, splat, m.team, visible, authoritative: true,
                               damage: 0f, splashes: splashes, ballistics: ballistics, maxDistance: range, blast: blast,
                               trailSpacing: trailSpacing, trailSize: trailSize, look: look, source: m.name);

    // Shooters (WeaponShooter): a main shot and shorter fillers between us and it (a share of them seen).
    void AddShooter(WeaponShooter w) => Add(w, "fireRate", m =>
    {
        float speed = Field<float>(w, "shotSpeed"), yawSpread = Field<float>(w, "yawSpread"), pitchSpread = Field<float>(w, "pitchSpread");
        float fillerSpread = Field<float>(w, "fillerSpread");
        int fillers = Field<int>(w, "fillerShots"), splat = Field<int>(w, "splashSize");
        Ballistics ballistics = Field<Ballistics>(w, "ballistics");
        float yaw = Random.Range(-yawSpread, yawSpread), pitch = AimRaise + Random.Range(-pitchSpread, pitchSpread);
        Fire(m, yaw, pitch, speed, splat, true, ballistics, Field<ShotLook>(w, "shotLook"));
        for (int i = 1; i <= fillers; i++)
            Fire(m, yaw + Random.Range(-fillerSpread, fillerSpread), pitch + Random.Range(-fillerSpread, fillerSpread),
                 speed * i / (fillers + 1f) + Random.Range(-0.1f, 0.1f), Mathf.RoundToInt(splat * 0.7f),
                 Random.value < Field<float>(w, "fillersSeen"), ballistics, Field<ShotLook>(w, "fillerLook"));
    });

    // Blasters: one big shot, dripping ink as it goes, that goes off at its range (or what it hits).
    void AddBlaster(Blaster w) => Add(w, "fireRate", m =>
        Fire(m, 0f, AimRaise, Field<float>(w, "shotSpeed"), Field<int>(w, "shotSize"), true, Field<Ballistics>(w, "ballistics"),
             look: Field<ShotLook>(w, "shotLook"), splashes: false, range: Field<float>(w, "range"), blast: Field<InkBlast>(w, "blast"),
             trailSpacing: Field<float>(w, "trailSpacing"), trailSize: Field<int>(w, "trailSize")));

    // Rollers: two ground flicks (a wide fan of many small blobs), then an air flick (a long, narrow line).
    void AddRoller(Roller w) => Add(w, "flickRate", m =>
    {
        float pace = Field<float>(w, "flickPace");
        Ballistics ballistics = Field<Ballistics>(w, "ballistics").Faster(pace);
        bool inAir = m.fired % 3 == 2;
        string kind = inAir ? "air" : "ground";
        int n = Field<int>(w, kind + "Droplets"), splat = Field<int>(w, kind + "Splat");
        float fan = Field<float>(w, kind + "Fan"), flickPitch = Field<float>(w, kind + "Pitch");
        Vector2 speeds = Field<Vector2>(w, kind + "Speed");
        ShotLook look = Field<ShotLook>(w, inAir ? "airLook" : "groundLook");
        for (int i = 0; i < n; i++)
        {
            float k = n > 1 ? i / (n - 1f) : 0.5f;
            float yaw = inAir ? Random.Range(-fan, fan) * 0.5f : Mathf.Lerp(-fan, fan, k) * 0.5f + Random.Range(-3f, 3f);
            float pitch = flickPitch + AimRaise + Random.Range(-3f, 3f);
            float speed = inAir ? Mathf.Lerp(speeds.x, speeds.y, k) : Random.Range(speeds.x, speeds.y);
            Fire(m, yaw, pitch, speed * pace, splat, true, ballistics, look);
        }
    });

    // The Inkbrush: swipes across, alternating sides, leaning the way it swings.
    void AddBrush(Brush w) => Add(w, "swipeRate", m =>
    {
        float pace = Field<float>(w, "swipePace"), fan = Field<float>(w, "fan"), lean = Field<float>(w, "lean"), pitch = Field<float>(w, "swipePitch");
        int n = Field<int>(w, "droplets"), splat = Field<int>(w, "swipeSplat");
        Vector2 speed = Field<Vector2>(w, "dropletSpeed");
        Ballistics ballistics = Field<Ballistics>(w, "ballistics").Faster(pace);
        float sign = m.fired % 2 == 0 ? 1f : -1f;
        for (int i = 0; i < n; i++)
        {
            float k = n > 1 ? i / (n - 1f) : 0.5f;
            float yaw = Mathf.Lerp(-fan, fan, k) * 0.5f + lean * sign + Random.Range(-3f, 3f);
            Fire(m, yaw, pitch + AimRaise + Random.Range(-3f, 3f), Random.Range(speed.x, speed.y) * pace, splat, true, ballistics, Field<ShotLook>(w, "dropletLook"));
        }
    });

    // The Bucket: a swaying line of droplets, nearest to furthest, and a big blob out in front.
    void AddBucket(Bucket w) => Add(w, "sloshRate", m =>
    {
        float pace = Field<float>(w, "sloshPace"), pitch = Field<float>(w, "sloshPitch"), spread = Field<float>(w, "spread"), sway = Field<float>(w, "sway");
        int n = Field<int>(w, "droplets"), splat = Field<int>(w, "dropletSplat"), blobSplat = Field<int>(w, "blobSplat");
        Vector2 speed = Field<Vector2>(w, "dropletSpeed");
        Ballistics ballistics = Field<Ballistics>(w, "ballistics").Faster(pace);
        float phase = Random.Range(0f, Mathf.PI * 2f), bends = Random.Range(1f, 2f); // as the weapon's sway
        for (int i = 0; i < n; i++)
        {
            float k = n > 1 ? i / (n - 1f) : 1f;
            float yaw = Random.Range(-spread, spread) + sway * Mathf.Sin(k * Mathf.PI * bends + phase);
            Fire(m, yaw, pitch + AimRaise + Random.Range(-1.5f, 1.5f), Mathf.Lerp(speed.x, speed.y, k) * pace, splat, true, ballistics, Field<ShotLook>(w, "dropletLook"));
        }
        Fire(m, 0f, pitch + AimRaise, Field<float>(w, "blobSpeed") * pace, blobSplat, true, ballistics, Field<ShotLook>(w, "blobLook"));
    });

    // The Sprinkler: a model of it stuck to the floor (its scripts and colliders taken off), spun and
    // fired here as Sprinkler does: from both nozzles, along the floor and a little up, slower and
    // further apart as it winds down over its life (then it starts again).
    void AddSprinkler()
    {
        if (sprinklerPrefab == null || !sprinklerPrefab.TryGetComponent(out Sprinkler s)) return;
        var holder = new GameObject("Sprinkler (model)");
        holder.SetActive(false); // nothing on the copy wakes up
        holder.transform.SetParent(transform, false);
        holder.transform.position = sprinklerAt;
        GameObject model = Instantiate(sprinklerPrefab, holder.transform);
        model.transform.SetLocalPositionAndRotation(Vector3.zero, Quaternion.identity);
        Sprinkler copy = model.GetComponent<Sprinkler>();
        Transform head = Field<Transform>(copy, "head");
        Renderer[] tinted = Field<Renderer[]>(copy, "teamTinted");
        foreach (MonoBehaviour b in model.GetComponentsInChildren<MonoBehaviour>(true)) DestroyImmediate(b);
        foreach (Collider c in model.GetComponentsInChildren<Collider>(true)) DestroyImmediate(c);
        foreach (Rigidbody r in model.GetComponentsInChildren<Rigidbody>(true)) DestroyImmediate(r);
        holder.SetActive(true);

        var m = new Mimic { name = "Sprinkler", key = "F2", at = sprinklerAt, team = 2 };
        if (tinted != null) foreach (Renderer r in tinted) if (r != null) tints.Add((r, m));
        float age = 0f, next = 0f;
        m.tick = (mm, dt) =>
        {
            float life = Field<float>(s, "lifetime"), lift = Field<float>(s, "shotLift");
            age += dt;
            if (age > life) { age = 0f; next = 0f; } // spent: a fresh one
            float wind = age / life;
            if (head != null) head.Rotate(0f, Mathf.Lerp(Field<float>(s, "spinStart"), Field<float>(s, "spinEnd"), wind) * dt, 0f, Space.Self);
            next -= dt;
            if (next > 0f || head == null) return;
            next += Mathf.Lerp(Field<float>(s, "fireIntervalStart"), Field<float>(s, "fireIntervalEnd"), wind);
            float speed = Mathf.Lerp(Field<float>(s, "shotSpeedStart"), Field<float>(s, "shotSpeedEnd"), wind);
            for (int side = -1; side <= 1; side += 2)
            {
                Vector3 along = head.forward * side;
                Vector3 dir = (along + model.transform.up * lift).normalized;
                ProjectileManager.Fire(head.position + along * 0.25f, dir * speed, Field<int>(s, "splashSize"), mm.team, damage: 0f, source: "Sprinkler", look: Field<ShotLook>(s, "shotLook"));
            }
        };
        m.show = shown => holder.SetActive(shown);
        mimics.Add(m);
    }

    // The Inkstrike: the real one, called in on its spot every few seconds (it paints the lab).
    void AddInkStrike()
    {
        if (inkStrikePrefab == null) return;
        mimics.Add(new Mimic
        {
            name = "Inkstrike", key = "F3", at = inkStrikeAt, team = 1, rate = inkStrikeEvery, next = 0.5f,
            fire = m => { m.rate = inkStrikeEvery; Instantiate(inkStrikePrefab).Begin(0, m.team, m.at, true); },
        });
    }

    void Update()
    {
        Keyboard k = Keyboard.current;
        if (k != null)
        {
            if (k.pKey.wasPressedThisFrame) paused = !paused;
            if (k.leftBracketKey.wasPressedThisFrame) TimeScale = SlowSteps.Where(t => t < timeScale - 1e-4f).DefaultIfEmpty(SlowSteps[0]).Last();
            if (k.rightBracketKey.wasPressedThisFrame) TimeScale = SlowSteps.Where(t => t > timeScale + 1e-4f).DefaultIfEmpty(1f).First();
            if (k.tKey.wasPressedThisFrame)
            {
                if (timeScale < 1f) { lastSlow = timeScale; timeScale = 1f; }
                else timeScale = lastSlow;
            }
            if (k.cKey.wasPressedThisFrame) colourSet++;
            if (k.xKey.wasPressedThisFrame) foreach (SurfaceInkManager s in surfaces) if (s != null) s.ClearInk();
            if (k.digit0Key.wasPressedThisFrame) foreach (Mimic m in mimics) m.on = true;
            for (int i = 0, n = 0; i < mimics.Count; i++)
            {
                Mimic m = mimics[i];
                if (m.key == null ? n < 9 && k[Key.Digit1 + n++].wasPressedThisFrame
                                  : m.key == "F2" ? k.f2Key.wasPressedThisFrame : m.key == "F3" && k.f3Key.wasPressedThisFrame)
                    m.on = !m.on;
            }
        }
        if (appliedSet != colourSet && Time.frameCount > 1) ApplyColours(); // after the surfaces have made their materials
        Time.timeScale = paused ? 0f : timeScale;
        float dt = Time.deltaTime;
        foreach (Mimic m in mimics)
        {
            m.show?.Invoke(m.on);
            if (Swatches) continue;
            if (m.tick != null)
            {
                if (m.on && dt > 0f) m.tick(m, dt);
                continue;
            }
            m.next -= dt;
            if (m.next > 0f) continue;
            m.next += m.rate;
            if (!m.on) continue;
            m.fire(m);
            m.fired++;
        }
        if (Swatches)
            for (int t = 0; t < 4; t++) FluidRenderer.Drop(SwatchSpots[t] + Vector3.up * 0.1f, SwatchRadius, TeamColour(t + 1)); // a dome: mostly under the floor
    }

    // The surfaces' ink and the models in the lab's colours (in the game NetGameManager sets these).
    void ApplyColours()
    {
        appliedSet = colourSet;
        var block = new MaterialPropertyBlock();
        foreach ((Renderer r, Mimic m) in tints)
        {
            if (r == null) continue;
            block.SetColor("_Color", TeamColour(m.team));
            block.SetColor("_EmissionColor", TeamColour(m.team) * 0.6f);
            r.SetPropertyBlock(block);
        }
        foreach (SurfaceInkManager s in surfaces)
        {
            if (s == null || !s.TryGetComponent(out Renderer r)) continue;
            r.material.SetColor("_AlphaColor", TeamColour(1));
            r.material.SetColor("_BetaColor", TeamColour(2));
            r.material.SetColor("_GammaColor", TeamColour(3));
            r.material.SetColor("_DeltaColor", TeamColour(4));
        }
    }

    // ── Colour matching ────────────────────────────────────────────────────

    // Firing stops and each team gets a still dome of fluid on a patch of floor painted its colour
    // (the dome's top faces up, as the floor does: they should match).
    public bool Swatches { get; private set; }
    public static readonly Vector3[] SwatchSpots = { new Vector3(-6f, 0f, 6f), new Vector3(-2f, 0f, 6f), new Vector3(2f, 0f, 6f), new Vector3(6f, 0f, 6f) };
    public const float SwatchRadius = 0.7f;
    public void ShowSwatches()
    {
        Swatches = true;
        foreach (Mimic m in mimics) m.on = false;
        foreach (SurfaceInkManager s in surfaces)
        {
            if (s == null || s.name != "Floor") continue;
            s.ClearInk();
            for (int t = 0; t < 4; t++)
            {
                Vector3 p = SwatchSpots[t];
                var uv = new Vector2((p.x + 25f) / 50f, (p.z + 12f) / 50f); // the floor quad: 50 x 40m from (-25, -12), UVs per 50m
                s.FillRegion(new Rect(uv.x - 0.035f, uv.y - 0.035f, 0.07f, 0.07f), t + 1);
            }
        }
    }

    void OnGUI()
    {
        if (Event.current.type == EventType.Repaint) fps = Mathf.Lerp(fps, 1f / Mathf.Max(Time.unscaledDeltaTime, 1e-4f), 0.05f);
        var style = new GUIStyle(GUI.skin.box) { fontSize = 15, alignment = TextAnchor.UpperLeft };
        style.normal.textColor = Color.white;
        FluidRenderer r = FluidRenderer.Active.Count > 0 ? FluidRenderer.Active[0] : null;
        NetGameManager.TeamColours[] sets = NetGameManager.DefaultColourSets();
        GUI.Box(new Rect(10, 10, 700, 54),
            $"Fluid lab (P pause{(paused ? ": PAUSED" : "")}, 1-9 weapons, F2 sprinkler, F3 inkstrike, 0 all, C colours: {sets[((colourSet % sets.Length) + sets.Length) % sets.Length].name}, X clear ink)\n" +
            $"{ProjectileManager.LiveCount} shots, {InkFluid.Live} drops, {(r != null ? r.Drawn : 0)} spheres drawn, {fps:0} fps; hold right mouse + WASD/QE to fly, R resets", style);
        GUI.Box(new Rect(10, 68, 300, 30), $"Time x{timeScale:0.##}  ([ ] slower/faster, T toggles)", style);
        TimeScale = GUI.HorizontalSlider(new Rect(10, 102, 300, 16), timeScale, SlowSteps[0], 1f);

        // Each mimic's name over where it fires from.
        Camera cam = Camera.main;
        if (cam == null) return;
        var label = new GUIStyle(GUI.skin.label) { fontSize = 14, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
        for (int i = 0; i < mimics.Count; i++)
        {
            Mimic m = mimics[i];
            Vector3 p = cam.WorldToScreenPoint(m.at + Vector3.up * 0.8f);
            if (p.z <= 0f) continue;
            string text = $"{m.key ?? (i + 1).ToString()} {m.name}";
            var rect = new Rect(p.x - 80f, Screen.height - p.y - 12f, 160f, 24f);
            label.normal.textColor = new Color(0f, 0f, 0f, 0.7f);
            GUI.Label(new Rect(rect.x + 1f, rect.y + 1f, rect.width, rect.height), text, label);
            label.normal.textColor = m.on ? Color.white : new Color(1f, 1f, 1f, 0.35f);
            GUI.Label(rect, text, label);
        }
    }
}

using System;
using System.Linq;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.InputSystem;
using Random = UnityEngine.Random;

// The fluid lab (Assets/Fluid/Lab/FluidLab.unity): every main weapon's attacks mimicked side by side,
// firing ahead (+z) on a loop over an inkable floor and back wall, to tune FluidRenderer's look
// against the whole set, apart from the game. The mimics are built from the real weapon prefabs (the
// player's loadout; their settings read directly): fire rates, speeds, counts, spreads, splat sizes,
// the flick paces, and each shot flown with its weapon's own ballistics (speed and gravity over its
// age, as ProjectileManager flies them), in the lab's own little simulation. They're read as each
// shot fires, so edits to the prefabs (or the lab) show at once. Four teams (one of the game's
// colour sets; teams 1-4 across the row). Shots are drawn a share of the size of the splat they
// paint, at the floor's ink scale (blobFill), so the ink in the air matches the paint.
// The look: each shot is a big blob travelling with a few companion blobs (a little smaller, with a
// slight starting difference in speed and direction, so they drift apart a touch); where shots land
// they paint (their weapon's splat size) and splatter: a few blobs thrown up and out, and a quick
// puddle that spreads and sinks into the paint. Blasters go off entirely in blobs: a core that swells
// and collapses, and a shell of blobs flung out that slow, fall and paint where they land. As in the
// game, shooters' unseen filler shots and blasters' trail drips paint without being drawn.
// Past the row's ends: a Sprinkler (a model, spinning and spraying as the sub does, winding down over
// its life) and an Inkstrike (the real one, its particles drawn as fluid; see InkStrike.LateUpdate).
// Keys: F1 cycles the look (the screen-space fluid, the game's current per-shot spheres, or both);
// P pauses; [ and ] slow time down and speed it up (T toggles); 1-9 toggle each weapon, F2 the
// sprinkler, F3 the inkstrike, 0 turns them all on; C changes the colour set; X clears the ink.
// The camera flies (FlyCamera) at its own speed whatever the time scale.
public class FluidLab : MonoBehaviour
{
    public enum Look { Fluid, Spheres, Both }
    public enum Kind { Shot, Roller, Bucket }

    class Mimic
    {
        public string name;
        public Vector3 at;
        public int team;
        public float rate, next;
        public bool on = true;
        public int fired;
        public Action<Mimic> fire;
        public Kind kind;                 // whose companion settings its shots use
        public float blobSize = 1f;       // drawn shots' size (share of the usual)
        public string key;                // its toggle (null: its number in the row)
        public Action<Mimic, float> tick; // runs every frame while on, instead of firing on `rate`
        public Action<bool> show;         // its model shown (on) or hidden (off)
    }

    class InkDrop
    {
        public Vector3 position, start, launch, fall, velocity; // launch x ballistics speed curve + fall: its velocity (ballistic drops)
        public Ballistics ballistics;    // null: plain gravity (x gravity) and drag
        public float radius, gravity, drag, age, seed, life = 4f, range;
        public int team, splat;          // paints this splat size where it lands (0: none)
        public bool splashes;            // splatters where it lands
        public bool hidden;              // paints, isn't drawn (the game's unseen fillers and drips)
        public bool puddle;              // spreading on a surface: slides, slows and sinks away
        public bool core;                // a blast's core: swells, then collapses
        public float shrink;             // shrinks away over this last share of its life (0: keeps its size)
        public InkBlast blast;           // goes off at its range or where it hits
        public float trailSpacing, sinceDrip; public int trailSize; // drips unseen ink along its path
        public ProjectileVisual sphere;
        public Vector3 Velocity => ballistics != null ? launch * ballistics.velocityOverTime.Evaluate(age) + fall : velocity;
    }

    [SerializeField] public Look look = Look.Fluid;
    [SerializeField] GameObject projectilePrefab;    // the game's shot, for the Spheres look
    [SerializeField] Weapon[] weapons;               // the prefabs mimicked (the player's loadout, in its order)
    [SerializeField] int colourSet = 1;              // NetGameManager.DefaultColourSets()
    [Header("Shots")]
    [SerializeField] float blobFill = 0.3f;          // a drawn shot's radius, as a share of the radius of the splat it paints (on the floor, at its ink scale)
    [SerializeField] float blasterShotSize = 0.6f;   // a blaster's shot (which doesn't paint itself), x that for its shot size
    [SerializeField] float stretch = 0.1f;           // teardrop length per m/s (x radius)
    [SerializeField] float wobble = 0.12f;           // radius wobble as it flies (share of its radius)
    [SerializeField] int companions = 2;             // blobs travelling with each shot (rollers and the bucket have their own, below)...
    [SerializeField] Vector2 companionSize = new Vector2(0.5f, 0.85f); // ...this big (share of the shot)...
    [SerializeField] float companionSpread = 2f;     // ...off its direction by up to this (degrees)...
    [SerializeField] float companionSpeed = 0.05f;   // ...and its speed by up to this share
    [SerializeField] float rollerBlobSize = 0.5f;    // rollers' ground flick blobs drawn this big (share; there are many)...
    [SerializeField] int rollerCompanions = 4;       // ...and rollers' companions
    [SerializeField] int bucketCompanions = 3;       // the bucket's companions...
    [SerializeField] float bucketCompanionSpread = 1f;  // ...up and down (degrees)...
    [SerializeField] float bucketCompanionYaw = 3f;     // ...across its line (its sway is the weapon's)...
    [SerializeField] float bucketCompanionSpeed = 0.1f; // ...and along it (share of the speed): one long body of ink
    [Header("Sub and special")]
    [SerializeField] GameObject sprinklerPrefab;
    [SerializeField] InkStrike inkStrikePrefab;
    [SerializeField] Vector3 sprinklerAt = new Vector3(18.5f, 0f, 14f);
    [SerializeField] Vector3 inkStrikeAt = new Vector3(-18.5f, 0f, 14f);
    [SerializeField] float inkStrikeEvery = 6f;      // seconds
    [Header("Splatter")]
    [SerializeField] Vector2Int crownBlobs = new Vector2Int(2, 4); // blobs thrown up where a shot lands (more for bigger shots)
    [SerializeField] Vector2 crownSize = new Vector2(0.35f, 0.55f); // share of the shot
    [SerializeField] float crownKick = 2.5f;         // how hard (m/s, off the surface)
    [SerializeField] int puddleDrops = 6;            // the spreading puddle: drops in its ring
    [SerializeField] float puddleTime = 0.22f;       // seconds before it's sunk into the paint
    [Header("Blasts")]
    [SerializeField] float coreReach = 0.45f;        // the core's widest radius (share of the blast's)...
    [SerializeField] float coreTime = 0.35f;         // ...swelling and collapsing
    [SerializeField] int coreBlobs = 6;              // the core is several blobs...
    [SerializeField] Vector2 coreSize = new Vector2(0.55f, 0.8f); // ...this big (share of coreReach)...
    [SerializeField] float coreScatter = 0.2f;       // ...set this far off the centre (share of the blast's radius): a lumpy surface
    [SerializeField] int shellBlobs = 18;
    [SerializeField] Vector2 shellSize = new Vector2(0.1f, 0.16f);  // radius, share of the blast's
    [SerializeField] Vector2 shellSpeed = new Vector2(4f, 6f);      // blast radii per second
    [SerializeField] float shellLife = 0.2f;                        // gone quickly, with no gravity: the burst, not falling ink...
    [SerializeField, Range(0f, 1f)] float shellShrink = 0.5f;       // ...shrinking away over this last share of it
    [SerializeField, Range(0f, 1f)] float fillersSeen = 0.33f;      // shooters' filler shots drawn (the game draws one in ten; the rest only paint)
    [SerializeField] bool paused;
    [SerializeField, Range(0.02f, 1f)] float timeScale = 1f;        // slow motion ([ and ], T toggles): Time.timeScale, so the inkstrike slows too; the camera keeps its speed

    public static FluidLab Instance { get; private set; }
    public int Live => drops.Count;
    public bool Paused { get => paused; set => paused = value; } // freezes the drops (P), to compare looks on one moment
    public float TimeScale { get => timeScale; set => timeScale = Mathf.Clamp(value, SlowSteps[0], 1f); }
    static readonly float[] SlowSteps = { 0.02f, 0.05f, 0.1f, 0.25f, 0.5f, 1f };
    float lastSlow = 0.25f;
    public int ColourSet { get => colourSet; set => colourSet = value; }

    readonly List<Mimic> mimics = new List<Mimic>();
    readonly List<InkDrop> drops = new List<InkDrop>();
    readonly Stack<ProjectileVisual> spare = new Stack<ProjectileVisual>();
    SurfaceInkManager[] surfaces = new SurfaceInkManager[0];
    int appliedSet = -1;

    const float AimRaise = 5f;     // the game aims this far above the view (level here)

    // A drawn shot's radius: a share of its splat's radius on the floor (the lab's ink scale, as the
    // maps' should be), so the ink in the air matches the paint it leaves.
    float Radius(float splatSize, float look = 1f) => splatSize * InkMetresPerSize * blobFill * look;
    float InkMetresPerSize
    {
        get
        {
            foreach (SurfaceInkManager s in surfaces) if (s != null && s.name == "Floor") return s.SplatMetresPerSize;
            return 2.5f / SurfaceInkManager.ReferenceTexelsPerMetre; // the maps'
        }
    }

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

    void OnDestroy() => Time.timeScale = 1f;

    void Start() => surfaces = FindObjectsByType<SurfaceInkManager>(FindObjectsSortMode.None);

    // A weapon's mimic. Everything is read from the prefab as it fires, so editing it (or the lab)
    // while playing shows at once.
    void Add(Weapon w, string rate, Action<Mimic> fire, Kind kind = Kind.Shot) =>
        mimics.Add(new Mimic
        {
            name = w.DisplayName, rate = Field<float>(w, rate), kind = kind,
            fire = m => { m.rate = Field<float>(w, rate); fire(m); },
        });

    // Shooters (WeaponShooter): a main shot and shorter fillers between us and it (a share of them seen).
    void AddShooter(WeaponShooter w) => Add(w, "fireRate", m =>
    {
        float speed = Field<float>(w, "shotSpeed"), yawSpread = Field<float>(w, "yawSpread"), pitchSpread = Field<float>(w, "pitchSpread");
        float fillerSpread = Field<float>(w, "fillerSpread");
        int fillers = Field<int>(w, "fillerShots"), splat = Field<int>(w, "splashSize");
        Ballistics ballistics = Field<Ballistics>(w, "ballistics");
        float yaw = Random.Range(-yawSpread, yawSpread), pitch = AimRaise + Random.Range(-pitchSpread, pitchSpread);
        Shot(m, yaw, pitch, speed, ballistics, Radius(splat), splat, true);
        for (int i = 1; i <= fillers; i++)
        {
            int size = Mathf.RoundToInt(splat * 0.7f);
            Shot(m, yaw + Random.Range(-fillerSpread, fillerSpread), pitch + Random.Range(-fillerSpread, fillerSpread),
                 speed * i / (fillers + 1f) + Random.Range(-0.1f, 0.1f), ballistics, Radius(size), size, Random.value < fillersSeen);
        }
    });

    // Blasters: one big shot, dripping unseen ink as it goes, that goes off at its range (or what it hits).
    void AddBlaster(Blaster w) => Add(w, "fireRate", m =>
    {
        float speed = Field<float>(w, "shotSpeed"), range = Field<float>(w, "range");
        int size = Field<int>(w, "shotSize");
        InkDrop d = Shot(m, 0f, AimRaise, speed, Field<Ballistics>(w, "ballistics"), Radius(size, blasterShotSize), 0, true);
        foreach (InkDrop c in companionsOf) c.range = range; // gone where it goes off
        d.range = range;
        d.blast = Field<InkBlast>(w, "blast");
        d.splashes = false;
        d.trailSpacing = Field<float>(w, "trailSpacing");
        d.trailSize = Field<int>(w, "trailSize");
    });

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
        m.blobSize = inAir ? 1f : rollerBlobSize;
        for (int i = 0; i < n; i++)
        {
            float k = n > 1 ? i / (n - 1f) : 0.5f;
            float yaw = inAir ? Random.Range(-fan, fan) * 0.5f : Mathf.Lerp(-fan, fan, k) * 0.5f + Random.Range(-3f, 3f);
            float pitch = flickPitch + AimRaise + Random.Range(-3f, 3f);
            float speed = inAir ? Mathf.Lerp(speeds.x, speeds.y, k) : Random.Range(speeds.x, speeds.y);
            Shot(m, yaw, pitch, speed * pace, ballistics, Radius(splat), splat, true);
        }
    }, Kind.Roller);

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
            Shot(m, yaw, pitch + AimRaise + Random.Range(-3f, 3f), Random.Range(speed.x, speed.y) * pace, ballistics, Radius(splat), splat, true);
        }
    });

    // The Bucket: a line of droplets, nearest to furthest, and a big blob out in front (as much bigger
    // than the droplets as the prefab draws it).
    void AddBucket(Bucket w) => Add(w, "sloshRate", m =>
    {
        float pace = Field<float>(w, "sloshPace"), pitch = Field<float>(w, "sloshPitch"), spread = Field<float>(w, "spread"), sway = Field<float>(w, "sway");
        int n = Field<int>(w, "droplets"), splat = Field<int>(w, "dropletSplat"), blobSplat = Field<int>(w, "blobSplat");
        Vector2 speed = Field<Vector2>(w, "dropletSpeed");
        float blobSpeed = Field<float>(w, "blobSpeed"), blobLook = Field<float>(w, "blobScale") / Mathf.Max(0.01f, Field<float>(w, "dropletScale"));
        Ballistics ballistics = Field<Ballistics>(w, "ballistics").Faster(pace);
        // The line sways as the weapon's does: a gentle bend (a different one each slosh) and each droplet's own wobble.
        float phase = Random.Range(0f, Mathf.PI * 2f), bends = Random.Range(1f, 2f);
        for (int i = 0; i < n; i++)
        {
            float k = n > 1 ? i / (n - 1f) : 1f;
            float yaw = Random.Range(-spread, spread) + sway * Mathf.Sin(k * Mathf.PI * bends + phase);
            Shot(m, yaw, pitch + AimRaise + Random.Range(-1.5f, 1.5f), Mathf.Lerp(speed.x, speed.y, k) * pace, ballistics, Radius(splat), splat, true);
        }
        Shot(m, 0f, pitch + AimRaise, blobSpeed * pace, ballistics, Radius(blobSplat, blobLook), blobSplat, true);
    }, Kind.Bucket);

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
            int splat = Field<int>(s, "splashSize");
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
                ShotFrom(mm, head.position + along * 0.25f, dir * speed, null, Radius(splat), splat, true);
            }
        };
        m.show = shown => holder.SetActive(shown);
        mimics.Add(m);
    }

    // The Inkstrike: the real one, called in on its spot every few seconds (it paints the lab, and
    // its particles are drawn as fluid while a FluidRenderer is on).
    void AddInkStrike()
    {
        if (inkStrikePrefab == null) return;
        mimics.Add(new Mimic
        {
            name = "Inkstrike", key = "F3", at = inkStrikeAt, team = 1, rate = inkStrikeEvery, next = 0.5f,
            fire = m => { m.rate = inkStrikeEvery; Instantiate(inkStrikePrefab).Begin(0, m.team, m.at, true, TeamColour(m.team)); },
        });
    }

    readonly List<(Renderer renderer, Mimic mimic)> tints = new List<(Renderer, Mimic)>(); // models' team-coloured parts

    readonly List<InkDrop> companionsOf = new List<InkDrop>(); // the last Shot's (a blaster's stop at its range)

    // A shot (drawn, or unseen and only painting), with its companion blobs if it's seen.
    InkDrop Shot(Mimic m, float yaw, float pitch, float speed, Ballistics ballistics, float radius, int splat, bool visible) =>
        ShotFrom(m, m.at, Quaternion.Euler(-pitch, yaw, 0f) * Vector3.forward * speed, ballistics, radius, splat, visible);

    InkDrop ShotFrom(Mimic m, Vector3 from, Vector3 launch, Ballistics ballistics, float radius, int splat, bool visible)
    {
        var d = new InkDrop
        {
            position = from, start = from, launch = launch, ballistics = ballistics ?? Ballistics.Default, radius = radius * m.blobSize,
            team = m.team, splat = splat, splashes = visible, hidden = !visible, seed = Random.value * 100f,
        };
        drops.Add(d);
        companionsOf.Clear();
        if (!visible) return d;
        (int count, float upDown, float across, float jitter) = m.kind switch
        {
            Kind.Roller => (rollerCompanions, companionSpread, companionSpread, companionSpeed),
            Kind.Bucket => (bucketCompanions, bucketCompanionSpread, bucketCompanionYaw, bucketCompanionSpeed),
            _ => (companions, companionSpread, companionSpread, companionSpeed),
        };
        for (int i = 0; i < count; i++)
        {
            Quaternion nudge = Quaternion.Euler(Random.Range(-upDown, upDown), Random.Range(-across, across), 0f);
            companionsOf.Add(new InkDrop
            {
                position = from + Random.insideUnitSphere * d.radius * 0.3f, start = from,
                launch = nudge * launch * (1f + Random.Range(-jitter, jitter)), ballistics = d.ballistics,
                radius = d.radius * Random.Range(companionSize.x, companionSize.y), team = m.team, seed = Random.value * 100f,
            });
        }
        drops.AddRange(companionsOf);
        return d;
    }

    void Update()
    {
        Keyboard k = Keyboard.current;
        if (k != null)
        {
            if (k.f1Key.wasPressedThisFrame) look = (Look)(((int)look + 1) % 3);
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
            if (m.tick != null)
            {
                if (m.on) m.tick(m, dt);
                continue;
            }
            m.next -= dt;
            if (m.next > 0f) continue;
            m.next += m.rate;
            if (!m.on) continue;
            m.fire(m);
            m.fired++;
        }
        Step(dt);
        Show();
    }

    // The surfaces' ink in the lab's colours (in the game NetGameManager sets these).
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

    void Step(float dt)
    {
        if (dt <= 0f) return;
        int mask = ~LayerMask.GetMask("Projectile", "Ignore Raycast");
        for (int i = drops.Count - 1; i >= 0; i--)
        {
            InkDrop d = drops[i];
            d.age += dt;
            if (d.puddle || d.core)
            {
                d.velocity *= Mathf.Exp(-9f * dt); // a puddle spreads, slowing, along the surface
                d.position += d.velocity * dt;
                if (d.age > d.life) drops.RemoveAt(i);
                continue;
            }
            // Flown as ProjectileManager flies shots (ballistic), or plainly (splatter, blasts' blobs).
            Vector3 step;
            if (d.ballistics != null)
            {
                d.fall += Physics.gravity * (d.ballistics.gravityOverTime.Evaluate(d.age) * dt);
                step = d.Velocity * dt;
            }
            else
            {
                d.velocity += Physics.gravity * (d.gravity * dt);
                d.velocity *= Mathf.Exp(-d.drag * dt);
                step = d.velocity * dt;
            }
            Vector3 next = d.position + step;
            if (d.range > 0f && (next - d.start).magnitude >= d.range) next = d.start + (next - d.start).normalized * d.range;
            bool hit = Physics.Linecast(d.position, next, out RaycastHit h, mask, QueryTriggerInteraction.Ignore);
            Drip(d, step.magnitude);
            if (hit) Land(d, h);
            d.position = hit ? h.point : next;
            bool atRange = d.range > 0f && (d.position - d.start).magnitude >= d.range - 1e-3f;
            if (d.blast != null && (atRange || hit)) Explode(d, hit ? h.point + h.normal * 0.1f : d.position);
            if (atRange || hit || d.age > d.life || d.position.y < -1f)
            {
                if (d.sphere != null) { d.sphere.gameObject.SetActive(false); spare.Push(d.sphere); }
                drops.RemoveAt(i);
            }
        }
    }

    // A blaster's trail: unseen drips falling to paint under its path.
    void Drip(InkDrop d, float moved)
    {
        if (d.trailSpacing <= 0f) return;
        d.sinceDrip += moved;
        while (d.sinceDrip >= d.trailSpacing)
        {
            d.sinceDrip -= d.trailSpacing;
            drops.Add(new InkDrop { position = d.position, velocity = d.Velocity * 0.1f + Vector3.down * ProjectileManager.FallSpeed, gravity = 1f, team = d.team, splat = d.trailSize, hidden = true });
        }
    }

    // Where a drop lands: its splat painted, and (for shots) a splatter: a few blobs thrown up and
    // out, and a puddle that spreads along the surface and sinks into the paint.
    void Land(InkDrop d, RaycastHit hit)
    {
        if (d.splat > 0 && hit.collider.TryGetComponent(out SurfaceInkManager ink)) ink.SplatAt(hit, d.splat, d.team);
        if (!d.splashes) return;
        Vector3 n = hit.normal;
        Vector3 along = Vector3.ProjectOnPlane(d.Velocity, n);
        Vector3 t1 = Vector3.Cross(n, Mathf.Abs(n.y) < 0.9f ? Vector3.up : Vector3.right).normalized, t2 = Vector3.Cross(n, t1);
        int crown = Mathf.Clamp(Mathf.RoundToInt(Mathf.Lerp(crownBlobs.x, crownBlobs.y, d.radius / 0.4f)), crownBlobs.x, crownBlobs.y);
        for (int i = 0; i < crown; i++)
        {
            float a = (i + Random.value * 0.6f) / crown * Mathf.PI * 2f;
            Vector3 outward = t1 * Mathf.Cos(a) + t2 * Mathf.Sin(a);
            drops.Add(new InkDrop
            {
                position = hit.point + n * d.radius * 0.4f,
                velocity = n * crownKick * Random.Range(0.6f, 1.1f) + outward * Random.Range(0.8f, 2f) + along * 0.2f,
                radius = d.radius * Random.Range(crownSize.x, crownSize.y), gravity = 2.5f, team = d.team, splat = 0,
                seed = Random.value * 100f, life = 0.6f,
            });
        }
        for (int i = 0; i < puddleDrops; i++)
        {
            float a = (i + Random.value * 0.5f) / puddleDrops * Mathf.PI * 2f;
            Vector3 outward = t1 * Mathf.Cos(a) + t2 * Mathf.Sin(a);
            drops.Add(new InkDrop
            {
                position = hit.point + n * d.radius * 0.2f, velocity = outward * d.radius * Random.Range(8f, 12f) + along * 0.3f,
                radius = d.radius * 0.65f, team = d.team, puddle = true, seed = Random.value * 100f, life = puddleTime,
            });
        }
    }

    // A blaster's shot going off, all in blobs: a core that swells and collapses where it went off,
    // and a shell of blobs bursting out, slowing, gone in a moment (keeping their size, no gravity).
    // It paints as the game's blast does: what's within its ink reach below it, and unseen drops
    // falling round under it.
    void Explode(InkDrop d, Vector3 at)
    {
        InkBlast b = d.blast;
        float r = b.radius;
        for (int i = 0; i < coreBlobs; i++)
        {
            Vector3 off = (i == 0 ? Vector3.zero : Random.onUnitSphere) * r * coreScatter * Random.Range(0.6f, 1f);
            drops.Add(new InkDrop
            {
                position = at + off, velocity = off * 3f, radius = r * coreReach * Random.Range(coreSize.x, coreSize.y),
                team = d.team, core = true, seed = Random.value * 100f, life = coreTime * Random.Range(0.85f, 1.1f),
            });
        }
        for (int i = 0; i < shellBlobs; i++)
        {
            Vector3 dir = Random.onUnitSphere;
            dir.y = dir.y * 0.7f + 0.15f;
            drops.Add(new InkDrop
            {
                position = at + dir.normalized * r * 0.15f, velocity = dir.normalized * r * Random.Range(shellSpeed.x, shellSpeed.y),
                radius = r * Random.Range(shellSize.x, shellSize.y), gravity = 0f, drag = 3f, team = d.team,
                seed = Random.value * 100f, life = shellLife * Random.Range(0.85f, 1.15f), shrink = shellShrink,
            });
        }
        int mask = ~LayerMask.GetMask("Projectile", "Ignore Raycast");
        if (Physics.Raycast(at, Vector3.down, out RaycastHit below, b.inkReach, mask, QueryTriggerInteraction.Ignore) && below.collider.TryGetComponent(out SurfaceInkManager ink))
            ink.SplatAt(below, b.splashSize, d.team);
        for (int i = 0; i < b.fallingDrops; i++)
        {
            Vector2 off = Random.insideUnitCircle * b.fallSpread;
            drops.Add(new InkDrop { position = at + new Vector3(off.x, 0f, off.y), velocity = Vector3.down * ProjectileManager.FallSpeed, gravity = 1f, team = d.team, splat = b.splashSize, hidden = true });
        }
    }

    void Show()
    {
        bool fluid = look != Look.Spheres, spheres = look != Look.Fluid;
        if (Swatches && fluid)
            for (int t = 0; t < 4; t++) FluidRenderer.Drop(SwatchSpots[t] + Vector3.up * 0.1f, SwatchRadius, TeamColour(t + 1)); // a dome: mostly under the floor
        foreach (InkDrop d in drops)
        {
            if (d.hidden) continue;
            float r = d.radius * (1f + wobble * Mathf.Sin(d.age * 23f + d.seed));
            if (d.puddle) r *= 1f - d.age / d.life;                                     // sinking into the paint
            if (d.shrink > 0f) r *= Mathf.SmoothStep(0f, 1f, (d.life - d.age) / (d.shrink * d.life)); // shrinking away
            if (d.core) r = d.radius * Mathf.Sin(Mathf.Clamp01(d.age / d.life) * Mathf.PI) * (1f + wobble * Mathf.Sin(d.age * 31f + d.seed)); // swelling, then collapsing
            if (r <= 0.01f) continue;
            Color c = TeamColour(d.team);
            Vector3 v = d.Velocity;
            if (fluid) FluidRenderer.Streak(d.position, v, r, c, d.puddle || d.core ? 0f : stretch);
            if (spheres && !d.puddle && !d.core) ShowSphere(d, c, v);
            else if (d.sphere != null) { d.sphere.gameObject.SetActive(false); spare.Push(d.sphere); d.sphere = null; }
        }
    }

    // The game's current look: the Projectile prefab's visual, sized to the drop and stretched by speed.
    void ShowSphere(InkDrop d, Color colour, Vector3 velocity)
    {
        if (projectilePrefab == null) return;
        if (d.sphere == null)
        {
            d.sphere = spare.Count > 0 ? spare.Pop() : Instantiate(projectilePrefab, transform).GetComponent<ProjectileVisual>();
            d.sphere.gameObject.SetActive(true);
            const int size = 10;
            d.sphere.Show(colour, size, d.radius * 2f / (size * 0.035f));
        }
        d.sphere.Follow(d.position, velocity);
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
        foreach (InkDrop d in drops) if (d.sphere != null) { d.sphere.gameObject.SetActive(false); spare.Push(d.sphere); }
        drops.Clear();
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

    float fps = 60f;

    void OnGUI()
    {
        if (Event.current.type == EventType.Repaint) fps = Mathf.Lerp(fps, 1f / Mathf.Max(Time.unscaledDeltaTime, 1e-4f), 0.05f);
        var style = new GUIStyle(GUI.skin.box) { fontSize = 15, alignment = TextAnchor.UpperLeft };
        style.normal.textColor = Color.white;
        FluidRenderer r = FluidRenderer.Active.Count > 0 ? FluidRenderer.Active[0] : null;
        NetGameManager.TeamColours[] sets = NetGameManager.DefaultColourSets();
        GUI.Box(new Rect(10, 10, 700, 54),
            $"Fluid lab: {look} (F1 look, P pause{(paused ? ": PAUSED" : "")}, 1-9 weapons, F2 sprinkler, F3 inkstrike, 0 all, C colours: {sets[((colourSet % sets.Length) + sets.Length) % sets.Length].name}, X clear ink)\n" +
            $"{drops.Count} drops, {(r != null ? r.Drawn : 0)} spheres drawn, {fps:0} fps; hold right mouse + WASD/QE to fly, R resets", style);
        GUI.Box(new Rect(10, 68, 300, 30), $"Time x{timeScale:0.##}  ([ ] slower/faster, T toggles)", style);
        TimeScale = GUI.HorizontalSlider(new Rect(10, 102, 300, 16), timeScale, SlowSteps[0], 1f);

        // Each weapon's name over where it fires from.
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

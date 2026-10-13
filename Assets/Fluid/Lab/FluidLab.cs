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
// age, as ProjectileManager flies them), in the lab's own little simulation. Four teams (one of the
// game's colour sets; teams 1-4 across the row).
// The look: each shot is a big blob travelling with a few companion blobs (a little smaller, with a
// slight starting difference in speed and direction, so they drift apart a touch); where shots land
// they paint (their weapon's splat size) and splatter: a few blobs thrown up and out, and a quick
// puddle that spreads and sinks into the paint. Blasters go off entirely in blobs: a core that swells
// and collapses, and a shell of blobs flung out that slow, fall and paint where they land. As in the
// game, shooters' unseen filler shots and blasters' trail drips paint without being drawn.
// Keys: F1 cycles the look (the screen-space fluid, the game's current per-shot spheres, or both);
// P pauses the drops; 1-9 toggle each weapon, 0 turns them all on; C changes the colour set; X
// clears the ink. The camera flies (FlyCamera).
public class FluidLab : MonoBehaviour
{
    public enum Look { Fluid, Spheres, Both }

    class Mimic
    {
        public string name;
        public Vector3 at;
        public int team;
        public float rate, next;
        public bool on = true;
        public int fired;
        public Action<Mimic> fire;
        public int companions;            // blobs travelling with each shot (see FluidLab.companions)
        public float companionSpread, companionSpeed;
        public float companionYaw;        // their spread across (degrees; companionSpread is up and down)
        public float blobSize = 1f;       // drawn shots' size (share of the game's)
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
    [SerializeField] float sizeScale = 0.8f;         // x the game's shot sizes (its visuals are splat size x 0.035 x scale across)
    [SerializeField] float stretch = 0.1f;           // teardrop length per m/s (x radius)
    [SerializeField] float wobble = 0.12f;           // radius wobble as it flies (share of its radius)
    [SerializeField] int companions = 2;             // blobs travelling with each shot (rollers and the bucket have more, see their Add)...
    [SerializeField] Vector2 companionSize = new Vector2(0.5f, 0.85f); // ...this big (share of the shot)...
    [SerializeField] float companionSpread = 2f;     // ...off its direction by up to this (degrees)...
    [SerializeField] float companionSpeed = 0.05f;   // ...and its speed by up to this share
    [SerializeField] float rollerBlobSize = 0.5f;    // rollers' blobs drawn this big (share)...
    [SerializeField] int rollerCompanions = 14;      // ...and this many with each droplet (3x the blobs: 15 to a droplet)
    [SerializeField] float bucketWander = 2.5f;      // the slosh's line sways side to side by up to this (degrees)...
    [SerializeField] float bucketCompanionYaw = 3f;  // ...and its blobs spread across it by up to this
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
    [SerializeField, Range(0.02f, 1f)] float timeScale = 1f;        // slow motion ([ and ], T toggles); the camera keeps its speed

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

    // A shot's look in the game: splat size x 0.035 x visual scale across.
    float Radius(float splatSize, float visualScale = 1f) => splatSize * 0.035f * visualScale * 0.5f * sizeScale;

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
    }

    void Start() => surfaces = FindObjectsByType<SurfaceInkManager>(FindObjectsSortMode.None);

    void Add(Weapon w, float rate, Action<Mimic> fire, int companionCount = -1, float spread = -1f, float speedJitter = -1f, float spreadAcross = -1f, float blobSize = 1f) =>
        mimics.Add(new Mimic
        {
            name = w.DisplayName, rate = rate, fire = fire,
            companions = companionCount >= 0 ? companionCount : companions,
            companionSpread = spread >= 0f ? spread : companionSpread,
            companionSpeed = speedJitter >= 0f ? speedJitter : companionSpeed,
            companionYaw = spreadAcross >= 0f ? spreadAcross : spread >= 0f ? spread : companionSpread,
            blobSize = blobSize,
        });

    // Shooters (WeaponShooter): a main shot and shorter fillers between us and it (one in ten seen).
    void AddShooter(WeaponShooter w)
    {
        float speed = Field<float>(w, "shotSpeed"), yawSpread = Field<float>(w, "yawSpread"), pitchSpread = Field<float>(w, "pitchSpread");
        float fillerSpread = Field<float>(w, "fillerSpread");
        int fillers = Field<int>(w, "fillerShots"), splat = Field<int>(w, "splashSize");
        Ballistics ballistics = Field<Ballistics>(w, "ballistics");
        Add(w, Field<float>(w, "fireRate"), m =>
        {
            float yaw = Random.Range(-yawSpread, yawSpread), pitch = AimRaise + Random.Range(-pitchSpread, pitchSpread);
            Shot(m, yaw, pitch, speed, ballistics, Radius(splat), splat, true);
            for (int i = 1; i <= fillers; i++)
            {
                int size = Mathf.RoundToInt(splat * 0.7f);
                Shot(m, yaw + Random.Range(-fillerSpread, fillerSpread), pitch + Random.Range(-fillerSpread, fillerSpread),
                     speed * i / (fillers + 1f) + Random.Range(-0.1f, 0.1f), ballistics, Radius(size), size, Random.value < fillersSeen);
            }
        });
    }

    // Blasters: one big shot, dripping unseen ink as it goes, that goes off at its range (or what it hits).
    void AddBlaster(Blaster w)
    {
        float speed = Field<float>(w, "shotSpeed"), range = Field<float>(w, "range"), drips = Field<float>(w, "trailSpacing");
        int size = Field<int>(w, "shotSize"), dripSize = Field<int>(w, "trailSize");
        Ballistics ballistics = Field<Ballistics>(w, "ballistics");
        InkBlast blast = Field<InkBlast>(w, "blast");
        Add(w, Field<float>(w, "fireRate"), m =>
        {
            InkDrop d = Shot(m, 0f, AimRaise, speed, ballistics, Radius(size), 0, true);
            foreach (InkDrop c in companionsOf) c.range = range; // gone where it goes off
            d.range = range;
            d.blast = blast;
            d.splashes = false;
            d.trailSpacing = drips;
            d.trailSize = dripSize;
        });
    }

    // Rollers: two ground flicks (a wide fan), then an air flick (a long, narrow line).
    void AddRoller(Roller w)
    {
        float pace = Field<float>(w, "flickPace"), scale = Field<float>(w, "dropletScale");
        Ballistics ballistics = Field<Ballistics>(w, "ballistics").Faster(pace);
        var ground = (n: Field<int>(w, "groundDroplets"), fan: Field<float>(w, "groundFan"), pitch: Field<float>(w, "groundPitch"), speed: Field<Vector2>(w, "groundSpeed"), splat: Field<int>(w, "groundSplat"));
        var air = (n: Field<int>(w, "airDroplets"), fan: Field<float>(w, "airFan"), pitch: Field<float>(w, "airPitch"), speed: Field<Vector2>(w, "airSpeed"), splat: Field<int>(w, "airSplat"));
        Add(w, Field<float>(w, "flickRate"), m =>
        {
            bool inAir = m.fired % 3 == 2;
            var f = inAir ? air : ground;
            for (int i = 0; i < f.n; i++)
            {
                float k = f.n > 1 ? i / (f.n - 1f) : 0.5f;
                float yaw = inAir ? Random.Range(-f.fan, f.fan) * 0.5f : Mathf.Lerp(-f.fan, f.fan, k) * 0.5f + Random.Range(-3f, 3f);
                float pitch = f.pitch + AimRaise + Random.Range(-3f, 3f);
                float speed = inAir ? Mathf.Lerp(f.speed.x, f.speed.y, k) : Random.Range(f.speed.x, f.speed.y);
                Shot(m, yaw, pitch, speed * pace, ballistics, Radius(f.splat, scale), f.splat, true);
            }
        }, companionCount: rollerCompanions, blobSize: rollerBlobSize); // smaller blobs, more of them
    }

    // The Inkbrush: swipes across, alternating sides, leaning the way it swings.
    void AddBrush(Brush w)
    {
        float pace = Field<float>(w, "swipePace"), scale = Field<float>(w, "dropletScale"), fan = Field<float>(w, "fan"), lean = Field<float>(w, "lean");
        float pitch = Field<float>(w, "swipePitch");
        int n = Field<int>(w, "droplets"), splat = Field<int>(w, "swipeSplat");
        Vector2 speed = Field<Vector2>(w, "dropletSpeed");
        Ballistics ballistics = Field<Ballistics>(w, "ballistics").Faster(pace);
        Add(w, Field<float>(w, "swipeRate"), m =>
        {
            float sign = m.fired % 2 == 0 ? 1f : -1f;
            for (int i = 0; i < n; i++)
            {
                float k = n > 1 ? i / (n - 1f) : 0.5f;
                float yaw = Mathf.Lerp(-fan, fan, k) * 0.5f + lean * sign + Random.Range(-3f, 3f);
                Shot(m, yaw, pitch + AimRaise + Random.Range(-3f, 3f), Random.Range(speed.x, speed.y) * pace, ballistics, Radius(splat, scale), splat, true);
            }
        });
    }

    // The Bucket: a line of droplets, nearest to furthest, and a big blob out in front.
    void AddBucket(Bucket w)
    {
        float pace = Field<float>(w, "sloshPace"), pitch = Field<float>(w, "sloshPitch"), spread = Field<float>(w, "spread");
        int n = Field<int>(w, "droplets"), splat = Field<int>(w, "dropletSplat"), blobSplat = Field<int>(w, "blobSplat");
        Vector2 speed = Field<Vector2>(w, "dropletSpeed");
        float scale = Field<float>(w, "dropletScale"), blobSpeed = Field<float>(w, "blobSpeed"), blobScale = Field<float>(w, "blobScale");
        Ballistics ballistics = Field<Ballistics>(w, "ballistics").Faster(pace);
        Add(w, Field<float>(w, "sloshRate"), m =>
        {
            // The line sways: a gentle bend (a different one each slosh) as well as each droplet's own jitter.
            float phase = Random.Range(0f, Mathf.PI * 2f), bends = Random.Range(1f, 2f);
            for (int i = 0; i < n; i++)
            {
                float k = n > 1 ? i / (n - 1f) : 1f;
                float yaw = Random.Range(-spread, spread) + bucketWander * Mathf.Sin(k * Mathf.PI * bends + phase);
                Shot(m, yaw, pitch + AimRaise + Random.Range(-1.5f, 1.5f), Mathf.Lerp(speed.x, speed.y, k) * pace, ballistics, Radius(splat, scale), splat, true);
            }
            Shot(m, 0f, pitch + AimRaise, blobSpeed * pace, ballistics, Radius(blobSplat, blobScale), blobSplat, true);
        }, companionCount: 3, spread: 1f, speedJitter: 0.1f, spreadAcross: bucketCompanionYaw); // spread along the line (one long body of ink), a little across it
    }

    readonly List<InkDrop> companionsOf = new List<InkDrop>(); // the last Shot's (a blaster's stop at its range)

    // A shot (drawn, or unseen and only painting), with its companion blobs if it's seen.
    InkDrop Shot(Mimic m, float yaw, float pitch, float speed, Ballistics ballistics, float radius, int splat, bool visible)
    {
        Vector3 launch = Quaternion.Euler(-pitch, yaw, 0f) * Vector3.forward * speed;
        var d = new InkDrop
        {
            position = m.at, start = m.at, launch = launch, ballistics = ballistics ?? Ballistics.Default, radius = radius * m.blobSize,
            team = m.team, splat = splat, splashes = visible, hidden = !visible, seed = Random.value * 100f,
        };
        drops.Add(d);
        companionsOf.Clear();
        if (!visible) return d;
        for (int i = 0; i < m.companions; i++)
        {
            Quaternion nudge = Quaternion.Euler(Random.Range(-m.companionSpread, m.companionSpread), Random.Range(-m.companionYaw, m.companionYaw), 0f);
            companionsOf.Add(new InkDrop
            {
                position = m.at + Random.insideUnitSphere * d.radius * 0.3f, start = m.at,
                launch = nudge * launch * (1f + Random.Range(-m.companionSpeed, m.companionSpeed)), ballistics = d.ballistics,
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
            for (int i = 0; i < mimics.Count && i < 9; i++)
                if (k[Key.Digit1 + i].wasPressedThisFrame) mimics[i].on = !mimics[i].on;
        }
        if (appliedSet != colourSet && Time.frameCount > 1) ApplyColours(); // after the surfaces have made their materials
        float dt = paused ? 0f : Time.deltaTime * timeScale;
        foreach (Mimic m in mimics)
        {
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

    void OnGUI()
    {
        var style = new GUIStyle(GUI.skin.box) { fontSize = 15, alignment = TextAnchor.UpperLeft };
        style.normal.textColor = Color.white;
        FluidRenderer r = FluidRenderer.Active.Count > 0 ? FluidRenderer.Active[0] : null;
        NetGameManager.TeamColours[] sets = NetGameManager.DefaultColourSets();
        GUI.Box(new Rect(10, 10, 700, 54),
            $"Fluid lab: {look} (F1 look, P pause{(paused ? ": PAUSED" : "")}, 1-9 weapons, 0 all, C colours: {sets[((colourSet % sets.Length) + sets.Length) % sets.Length].name}, X clear ink)\n" +
            $"{drops.Count} drops, {(r != null ? r.Drawn : 0)} spheres drawn, {1f / Mathf.Max(Time.smoothDeltaTime, 1e-4f):0} fps; hold right mouse + WASD/QE to fly, R resets", style);
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
            string text = $"{i + 1} {m.name}";
            var rect = new Rect(p.x - 80f, Screen.height - p.y - 12f, 160f, 24f);
            label.normal.textColor = new Color(0f, 0f, 0f, 0.7f);
            GUI.Label(new Rect(rect.x + 1f, rect.y + 1f, rect.width, rect.height), text, label);
            label.normal.textColor = m.on ? Color.white : new Color(1f, 1f, 1f, 0.35f);
            GUI.Label(rect, text, label);
        }
    }
}

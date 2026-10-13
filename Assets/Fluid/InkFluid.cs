using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

// All ink in the air, drawn as fluid (FluidRenderer). ProjectileManager's seen shots are drawn from
// their own flight (DrawShot); everything that's only for the look is simulated here: the companion
// blobs travelling with each seen shot, the splatter where shots land (a crown of blobs and a puddle
// sinking into the paint), blasts (a lumpy core swelling and collapsing, a shell bursting out and
// shrinking away), splashes, and particle systems drawn as ink (DrawParticles). Nothing here paints
// or hurts: that's ProjectileManager's and InkExplosion's.
// Sizes follow the paint: a shot's radius is a share (blobFill) of its splat's, at the map's ink
// scale. One lives for the whole session; it puts a FluidRenderer on each main camera.
public class InkFluid : MonoBehaviour
{
    class Drop
    {
        public Vector3 position, start, launch, inherit, fall, velocity; // ballistic: launch x its speed curve + inherit + fall
        public Ballistics ballistics;    // null: plain velocity, gravity and drag
        public float radius, gravity, drag, age, seed, life, range, shrink, stretch;
        public Color colour;
        public bool puddle;              // spreading on a surface, slowing and sinking away
        public bool core;                // a blast's core: swells, then collapses
        public Vector3 Velocity => ballistics != null ? launch * ballistics.velocityOverTime.Evaluate(age) + inherit + fall : velocity;
    }

    const int MaxDrops = 4000;           // beyond this, new look-only drops are skipped

    static InkFluid instance;
    static InkFluid Instance
    {
        get
        {
            if (instance == null)
            {
                var go = new GameObject("InkFluid");
                DontDestroyOnLoad(go);
                instance = go.AddComponent<InkFluid>();
            }
            return instance;
        }
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot() => _ = Instance;

    static InkFluidSettings S => InkFluidSettings.Instance;

    readonly List<Drop> drops = new List<Drop>();
    readonly Stack<Drop> spare = new Stack<Drop>();
    ParticleSystem.Particle[] particles;
    float camerasCheckedAt = -10f;

    public static int Live => instance != null ? instance.drops.Count : 0;
    public static int Explosions { get; private set; }       // tests
    public static int LastExplosionBlobs { get; private set; }
    public static Vector3 LastBurstCarry { get; private set; } // tests

    // Team colours without a game manager (the fluid lab).
    public static System.Func<int, Color> ColourOverride;
    public static Color Colour(int team)
    {
        NetGameManager gm = NetGameManager.Instance;
        if (gm != null) return gm.TeamColour(team);
        if (ColourOverride != null) return ColourOverride(team);
        NetGameManager.TeamColours c = NetGameManager.DefaultColourSets()[0];
        return team switch { 2 => c.beta, 3 => c.gamma, 4 => c.delta, _ => c.alpha };
    }

    // ── Sizes ───────────────────────────────────────────────────────────────

    // Metres of splat radius per unit of splash size here (the scene's ink scale: its largest, the
    // level's own; the maps' if there are no surfaces).
    static float inkMetres = -1f, inkCheckedAt = -10f;
    public static float InkMetresPerSize
    {
        get
        {
            if (inkMetres <= 0f || Time.unscaledTime - inkCheckedAt > 2f)
            {
                inkCheckedAt = Time.unscaledTime;
                inkMetres = 0f;
                foreach (SurfaceInkManager s in FindObjectsByType<SurfaceInkManager>(FindObjectsSortMode.None))
                    inkMetres = Mathf.Max(inkMetres, s.SplatMetresPerSize);
                if (inkMetres <= 0f) inkMetres = 2.5f / SurfaceInkManager.ReferenceTexelsPerMetre;
            }
            return inkMetres;
        }
    }

    // A drawn shot's radius: a share of its splat's radius (x its weapon's look).
    public static float Radius(float splashSize, float look = 1f) => splashSize * InkMetresPerSize * S.blobFill * look;

    // ── Shots ───────────────────────────────────────────────────────────────

    // One of ProjectileManager's seen shots, this frame: a wobbling teardrop along its flight.
    public static void DrawShot(Vector3 position, Vector3 velocity, float radius, float age, float seed, Color colour, float stretch)
    {
        float r = radius * (1f + S.wobble * Mathf.Sin(age * 23f + seed));
        FluidRenderer.Streak(position, velocity, r, colour, stretch);
    }

    // A seen shot's companions (its look's): a few blobs leaving with it, a little smaller, with a
    // slight difference in direction and speed so they drift apart a touch. They fly its ballistics,
    // stop at its range (a blaster's goes off there) and vanish where they land.
    public static void Companions(Vector3 from, Vector3 launch, Vector3 inherit, Ballistics ballistics, float radius, Color colour, ShotLook look, float range)
    {
        Ballistics b = ballistics ?? Ballistics.Default;
        for (int i = 0; i < look.companions; i++)
        {
            Quaternion nudge = Quaternion.Euler(Random.Range(-look.upDownSpread, look.upDownSpread), Random.Range(-look.sideSpread, look.sideSpread), 0f);
            Drop d = Instance.New();
            if (d == null) return;
            d.position = from + Random.insideUnitSphere * radius * 0.3f;
            d.start = from;
            d.launch = nudge * launch * (1f + Random.Range(-look.speedSpread, look.speedSpread));
            d.inherit = inherit;
            d.ballistics = b;
            d.radius = radius * Random.Range(look.companionSize.x, look.companionSize.y);
            d.colour = colour;
            d.range = range;
            d.life = 10f;
            d.stretch = look.stretch;
        }
    }

    // ── Splatter, blasts, splashes ──────────────────────────────────────────

    // Where a shot lands: a crown of blobs thrown up and out, and a puddle that spreads along the
    // surface and sinks into the paint.
    public static void Splatter(Vector3 point, Vector3 normal, Vector3 velocity, float radius, Color colour)
    {
        InkFluid f = Instance;
        Vector3 along = Vector3.ProjectOnPlane(velocity, normal);
        Vector3 t1 = Vector3.Cross(normal, Mathf.Abs(normal.y) < 0.9f ? Vector3.up : Vector3.right).normalized, t2 = Vector3.Cross(normal, t1);
        int crown = Mathf.Clamp(Mathf.RoundToInt(Mathf.Lerp(S.crownBlobs.x, S.crownBlobs.y, radius / 0.4f)), S.crownBlobs.x, S.crownBlobs.y);
        for (int i = 0; i < crown; i++)
        {
            float a = (i + Random.value * 0.6f) / crown * Mathf.PI * 2f;
            Vector3 outward = t1 * Mathf.Cos(a) + t2 * Mathf.Sin(a);
            Drop d = f.New();
            if (d == null) return;
            d.position = point + normal * radius * 0.4f;
            d.velocity = normal * S.crownKick * Random.Range(0.6f, 1.1f) + outward * Random.Range(0.8f, 2f) + along * 0.2f;
            d.radius = radius * Random.Range(S.crownSize.x, S.crownSize.y);
            d.gravity = 2.5f;
            d.colour = colour;
            d.life = 0.6f;
        }
        for (int i = 0; i < S.puddleDrops; i++)
        {
            float a = (i + Random.value * 0.5f) / S.puddleDrops * Mathf.PI * 2f;
            Vector3 outward = t1 * Mathf.Cos(a) + t2 * Mathf.Sin(a);
            Drop d = f.New();
            if (d == null) return;
            d.position = point + normal * radius * 0.2f;
            d.velocity = outward * radius * Random.Range(8f, 12f) + along * 0.3f;
            d.radius = radius * 0.65f;
            d.colour = colour;
            d.puddle = true;
            d.life = S.puddleTime;
        }
    }

    // A blast, all in blobs: a lumpy core (several blobs set a little off the centre) swelling and
    // collapsing, and a shell bursting out, slowing and shrinking away in a moment (no gravity).
    // Returns how many blobs it took.
    public static int Explode(Vector3 at, float blastRadius, Color colour)
    {
        InkFluid f = Instance;
        float r = blastRadius;
        int made = 0;
        for (int i = 0; i < S.coreBlobs; i++)
        {
            Vector3 off = (i == 0 ? Vector3.zero : Random.onUnitSphere) * r * S.coreScatter * Random.Range(0.6f, 1f);
            Drop d = f.New();
            if (d == null) break;
            d.position = at + off;
            d.velocity = off * 3f;
            d.radius = r * S.coreReach * Random.Range(S.coreSize.x, S.coreSize.y);
            d.colour = colour;
            d.core = true;
            d.life = S.coreTime * Random.Range(0.85f, 1.1f);
            made++;
        }
        for (int i = 0; i < S.shellBlobs; i++)
        {
            Vector3 dir = Random.onUnitSphere;
            dir.y = dir.y * 0.7f + 0.15f;
            dir.Normalize();
            Drop d = f.New();
            if (d == null) break;
            d.position = at + dir * r * 0.15f;
            d.velocity = dir * r * Random.Range(S.shellSpeed.x, S.shellSpeed.y);
            d.radius = r * Random.Range(S.shellSize.x, S.shellSize.y);
            d.drag = 3f;
            d.colour = colour;
            d.life = S.shellLife * Random.Range(0.85f, 1.15f);
            d.shrink = S.shellShrink;
            made++;
        }
        Explosions++;
        LastExplosionBlobs = made;
        return made;
    }

    // A splash: `count` blobs thrown up off a surface (along `normal`) and out, falling back, each
    // also carrying some of `carry` (whatever made the splash, moving: its velocity).
    public static void Burst(Vector3 at, Vector3 normal, Color colour, int count, Vector2 radius, float speed, Vector3 carry = default)
    {
        InkFluid f = Instance;
        LastBurstCarry = carry;
        Vector3 t1 = Vector3.Cross(normal, Mathf.Abs(normal.y) < 0.9f ? Vector3.up : Vector3.right).normalized, t2 = Vector3.Cross(normal, t1);
        for (int i = 0; i < count; i++)
        {
            float a = (i + Random.value * 0.7f) / count * Mathf.PI * 2f;
            Drop d = f.New();
            if (d == null) return;
            d.position = at + normal * 0.05f;
            d.velocity = (normal * Random.Range(0.8f, 1.3f) + (t1 * Mathf.Cos(a) + t2 * Mathf.Sin(a)) * Random.Range(0.3f, 0.8f)) * speed
                       + carry * Random.Range(0.6f, 1f);
            d.radius = Random.Range(radius.x, radius.y);
            d.gravity = 1.5f;
            d.colour = colour;
            d.life = 0.8f;
            d.shrink = 0.4f;
        }
    }

    // A particle system's particles as ink this frame (its renderer should be off): each one a drop
    // `size` x its radius, in its colour (made opaque), stretched along its motion.
    public static void DrawParticles(ParticleSystem ps, float size, float stretch)
    {
        if (ps == null) return;
        InkFluid f = Instance;
        int max = ps.main.maxParticles;
        if (f.particles == null || f.particles.Length < max) f.particles = new ParticleSystem.Particle[max];
        int n = ps.GetParticles(f.particles);
        bool local = ps.main.simulationSpace == ParticleSystemSimulationSpace.Local;
        Transform t = ps.transform;
        for (int i = 0; i < n; i++)
        {
            ParticleSystem.Particle p = f.particles[i];
            Color c = p.GetCurrentColor(ps);
            c.a = 1f;
            Vector3 at = local ? t.TransformPoint(p.position) : p.position;
            Vector3 v = local ? t.TransformVector(p.totalVelocity) : p.totalVelocity;
            FluidRenderer.Streak(at, v, p.GetCurrentSize(ps) * 0.5f * size, c, stretch);
        }
    }

    // ── Simulation ──────────────────────────────────────────────────────────

    void Awake() => SceneManager.activeSceneChanged += (_, __) => Clear();

    void Clear()
    {
        foreach (Drop d in drops) spare.Push(d);
        drops.Clear();
    }

    Drop New()
    {
        if (drops.Count >= MaxDrops) return null;
        Drop d = spare.Count > 0 ? spare.Pop() : new Drop();
        d.position = d.start = d.launch = d.inherit = d.fall = d.velocity = Vector3.zero;
        d.ballistics = null;
        d.radius = d.gravity = d.drag = d.age = d.range = d.shrink = 0f;
        d.stretch = S.stretch;
        d.life = 1f;
        d.seed = Random.value * 100f;
        d.puddle = d.core = false;
        drops.Add(d);
        return d;
    }

    void Update()
    {
        if (Time.unscaledTime - camerasCheckedAt > 0.5f) EnsureCameras();
        float dt = Time.deltaTime;
        if (dt > 0f) Step(dt);
    }

    void LateUpdate() => Show(); // after everything has moved (shots draw themselves as they move)

    // Each main camera draws ink.
    void EnsureCameras()
    {
        camerasCheckedAt = Time.unscaledTime;
        foreach (Camera c in Camera.allCameras)
            if (c.CompareTag("MainCamera") && c.targetTexture == null && !c.TryGetComponent(out FluidRenderer _))
                c.gameObject.AddComponent<FluidRenderer>();
    }

    void Step(float dt)
    {
        int mask = PhysicsLayers.Environment;
        for (int i = drops.Count - 1; i >= 0; i--)
        {
            Drop d = drops[i];
            d.age += dt;
            bool done = d.age > d.life;
            if (!done && (d.puddle || d.core))
            {
                d.velocity *= Mathf.Exp(-9f * dt); // spreading, slowing
                d.position += d.velocity * dt;
            }
            else if (!done)
            {
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
                bool atRange = false;
                if (d.range > 0f && (next - d.start).magnitude >= d.range) { next = d.start + (next - d.start).normalized * d.range; atRange = true; }
                done = atRange || next.y < -20f || Physics.Linecast(d.position, next, mask, QueryTriggerInteraction.Ignore); // landed: gone
                d.position = next;
            }
            if (!done) continue;
            spare.Push(d);
            drops[i] = drops[drops.Count - 1];
            drops.RemoveAt(drops.Count - 1);
        }
    }

    void Show()
    {
        float wobble = S.wobble;
        foreach (Drop d in drops)
        {
            float r = d.radius * (1f + wobble * Mathf.Sin(d.age * 23f + d.seed));
            if (d.puddle) r *= 1f - d.age / d.life;                                     // sinking into the paint
            if (d.core) r = d.radius * Mathf.Sin(Mathf.Clamp01(d.age / d.life) * Mathf.PI) * (1f + wobble * Mathf.Sin(d.age * 31f + d.seed)); // swelling, then collapsing
            if (d.shrink > 0f) r *= Mathf.SmoothStep(0f, 1f, (d.life - d.age) / (d.shrink * d.life)); // shrinking away
            if (r <= 0.01f) continue;
            FluidRenderer.Streak(d.position, d.Velocity, r, d.colour, d.puddle || d.core ? 0f : d.stretch);
        }
    }
}

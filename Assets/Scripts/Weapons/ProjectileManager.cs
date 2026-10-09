using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Pool;

// Every ink projectile in flight. Shots are plain data, moved each frame in steps of at most
// MaxStep: launch velocity × its speed curve, plus the shooter's carried velocity, plus gravity
// built up through its gravity curve (see Ballistics). The stretch each step covers is swept for
// hits, so fast shots can't skip through anything. Only layers the Projectile layer collides with
// count (the physics matrix: grates let ink through). Enemy hitboxes and subs take damage,
// teammates' are passed through, and the first solid surface stops the shot (splatting it if it's
// inkable).
// Shots with a blast (see InkBlast) explode instead of splatting: on whatever they hit, or in
// mid-air once they've flown their maxDistance. Remote replays (authoritative = false) only fly,
// splash and show their explosions. Visuals are pooled per prefab; hidden
// shots have none.
public class ProjectileManager : MonoBehaviour
{
    public class Shot
    {
        public Vector3 position, launch, inherit, fallVelocity;
        public float age;
        public Ballistics ballistics;
        public GameObject prefab;
        public ProjectileVisual visual;
        public float radius, damage;
        public int splashSize, team;
        public bool authoritative, impactParticles;
        public ulong ownerId;
        public string source; // what fired it, for "splatted with"
        public float maxDistance, travelled; // explodes in mid-air at maxDistance (0: no limit)
        public InkBlast blast;                // explodes rather than splats
        public float trailSpacing, sinceDrip; // drops a drip of ink every trailSpacing metres it flies (0: none)
        public int trailSize;
        public HashSet<Object> hitGroup;      // shots sharing one: each target takes damage from the first of them only
        public DamageFalloff falloff;         // damage over its age (null: always `damage`)
        public Color colour;

        public float DamageNow => falloff != null ? falloff.At(age) : damage;
        public Vector3 Velocity => launch * ballistics.velocityOverTime.Evaluate(age) + inherit + fallVelocity;
    }

    const float MaxStep = 1f / 120f; // longest single movement step
    const float MaxLifetime = 10f;
    const float KillHeight = -10f;   // fell off the level

    // A shot ended by hitting something: where. Fired before the shot is recycled.
    public static event System.Action<Shot, Vector3> Impact;

    static ProjectileManager instance;
    static ProjectileManager Instance => instance != null ? instance : instance = new GameObject("ProjectileManager").AddComponent<ProjectileManager>();

    readonly List<Shot> live = new List<Shot>();
    readonly Stack<Shot> spare = new Stack<Shot>();
    readonly Dictionary<GameObject, ObjectPool<ProjectileVisual>> pools = new Dictionary<GameObject, ObjectPool<ProjectileVisual>>();
    readonly Dictionary<GameObject, ProjectileVisual> templates = new Dictionary<GameObject, ProjectileVisual>();
    readonly RaycastHit[] hits = new RaycastHit[16];
    static readonly HitDistance ByDistance = new HitDistance();
    int hitMask, visualsCreated;

    public static int LiveCount => instance != null ? instance.live.Count : 0;
    public static int VisualsCreated => instance != null ? instance.visualsCreated : 0; // tests: pooling

    void Awake()
    {
        hitMask = PhysicsLayers.Environment | LayerMask.GetMask("Hitbox");
        int projectile = LayerMask.NameToLayer("Projectile");
        if (projectile >= 0)
            for (int layer = 0; layer < 32; layer++)
                if (Physics.GetIgnoreLayerCollision(projectile, layer)) hitMask &= ~(1 << layer);
    }

    void OnDestroy()
    {
        if (instance == this) instance = null;
    }

    // Launches a shot: `launch` is its starting velocity (direction × speed), `inherit` the shooter's
    // carried velocity.
    public static Shot Fire(GameObject prefab, Vector3 origin, Vector3 launch, int splashSize, int team,
                            bool visible = true, bool authoritative = true, ulong ownerId = 0, float damage = 30f,
                            bool impactParticles = true, Ballistics ballistics = null, Vector3 inherit = default,
                            string source = null, float maxDistance = 0f, InkBlast blast = null,
                            float trailSpacing = 0f, int trailSize = 0, HashSet<Object> hitGroup = null, float visualScale = 1f,
                            DamageFalloff falloff = null)
    {
        ProjectileManager m = Instance;
        ProjectileVisual template = m.Template(prefab);
        Shot s = m.spare.Count > 0 ? m.spare.Pop() : new Shot();
        s.position = origin;
        s.launch = launch;
        s.inherit = inherit;
        s.fallVelocity = Vector3.zero;
        s.age = 0f;
        s.ballistics = ballistics ?? Ballistics.Default;
        s.prefab = prefab;
        s.radius = template != null ? template.HitRadius : 0.12f;
        s.damage = falloff != null ? falloff.damage : damage;
        s.falloff = falloff;
        s.splashSize = splashSize;
        s.team = team;
        s.authoritative = authoritative;
        s.impactParticles = impactParticles;
        s.ownerId = ownerId;
        s.source = source;
        s.maxDistance = maxDistance;
        s.travelled = 0f;
        s.blast = blast;
        s.trailSpacing = trailSpacing;
        s.trailSize = trailSize;
        s.sinceDrip = 0f;
        s.hitGroup = hitGroup;
        NetGameManager gm = NetGameManager.Instance;
        s.colour = gm == null ? Color.white : team == 1 ? gm.AlphaTeam : gm.BetaTeam;
        s.visual = visible && template != null ? m.pools[prefab].Get() : null;
        if (s.visual != null)
        {
            s.visual.Show(s.colour, splashSize, visualScale);
            s.visual.Follow(origin, s.Velocity);
        }
        m.live.Add(s);
        return s;
    }

    void Update()
    {
        float dt = Time.deltaTime;
        if (dt <= 0f) return;
        for (int i = live.Count - 1; i >= 0; i--) // removal swaps in the last one, already done
        {
            Shot s = live[i];
            if (Advance(s, dt)) { Retire(i); continue; }
            if (s.visual != null) s.visual.Follow(s.position, s.Velocity);
        }
    }

    // True once the shot is done (hit something, fell away or flew too long).
    bool Advance(Shot s, float dt)
    {
        int steps = Mathf.CeilToInt(dt / MaxStep);
        float h = dt / steps;
        for (int k = 0; k < steps; k++)
        {
            s.fallVelocity += Physics.gravity * (s.ballistics.gravityOverTime.Evaluate(s.age) * h);
            s.age += h;
            Vector3 step = s.Velocity * h;
            bool lastStep = false;
            if (s.maxDistance > 0f)
            {
                float left = s.maxDistance - s.travelled, length = step.magnitude;
                if (length >= left) { step *= left / Mathf.Max(length, 1e-6f); lastStep = true; }
            }
            Vector3 next = s.position + step;
            if (Sweep(s, s.position, next)) return true;
            s.position = next;
            s.travelled += step.magnitude;
            Drip(s, step.magnitude);
            if (lastStep)
            {
                Detonate(s, s.position, null); // as far as it goes
                Impact?.Invoke(s, s.position);
                return true;
            }
            if (s.age > MaxLifetime || s.position.y < KillHeight) return true;
        }
        return false;
    }

    // Nearest hit along from→to that stops the shot, applied. True if one did.
    bool Sweep(Shot s, Vector3 from, Vector3 to)
    {
        Vector3 delta = to - from;
        float length = delta.magnitude;
        if (length < 1e-5f) return false;
        Vector3 dir = delta / length;
        int n = Physics.SphereCastNonAlloc(from, s.radius, dir, hits, length, hitMask, QueryTriggerInteraction.Collide);
        System.Array.Sort(hits, 0, n, ByDistance);
        bool barrier = SpawnPad.Stops(s.team, from, dir, length, out float barrierAt, out SpawnPad pad); // the other team's spawn barrier
        for (int i = 0; i < n; i++)
        {
            RaycastHit h = hits[i];
            if (barrier && h.distance > barrierAt) break; // the barrier's nearer
            Collider c = h.collider;
            bool overlapping = h.distance <= 0f && h.point == Vector3.zero; // the step started inside it
            Vector3 point = overlapping ? from : h.point;

            SubDevice device = c.GetComponentInParent<SubDevice>();
            if (device != null)
            {
                if (device.Team == s.team || s.damage <= 0f) continue; // drips fall through
                if (s.authoritative && FirstHit(s, device)) device.TakeDamage(s.DamageNow, s.team, s.ownerId);
                Detonate(s, from + dir * Mathf.Max(0f, h.distance), device);
                Impact?.Invoke(s, point);
                return true;
            }
            PlayerHitbox hitbox = c.GetComponentInParent<PlayerHitbox>();
            if (hitbox != null)
            {
                if (hitbox.Team == s.team || s.damage <= 0f) continue; // teammates and the shooter; drips fall through
                if (s.authoritative && FirstHit(s, hitbox)) hitbox.TakeDamage(s.DamageNow, s.team, s.ownerId, s.source);
                Detonate(s, from + dir * Mathf.Max(0f, h.distance), hitbox); // they took the direct hit
                Impact?.Invoke(s, point);
                return true;
            }
            if (c.isTrigger) continue; // volumes don't stop ink

            if (s.blast != null) Detonate(s, overlapping ? from : h.point + h.normal * 0.1f, null);
            else Land(s, c, overlapping ? new Ray(from - dir * 0.5f, dir) : new Ray(h.point + h.normal * 0.5f, -h.normal));
            Impact?.Invoke(s, point);
            return true;
        }
        if (barrier)
        {
            // Stopped on the barrier: no ink, no hit inside (a blast still goes off, out here).
            Vector3 at = from + dir * barrierAt;
            pad.Pulse(at);
            Detonate(s, at - dir * 0.1f, null);
            if (s.impactParticles && templates.TryGetValue(s.prefab, out ProjectileVisual t) && t != null)
                InkParticles.Spawn(t.SplashParticles, at, Quaternion.FromToRotation(Vector3.up, (at - pad.Centre).normalized), s.colour);
            Impact?.Invoke(s, at);
            return true;
        }
        return false;
    }

    static bool FirstHit(Shot s, Object target) => s.hitGroup == null || s.hitGroup.Add(target);

    // Splats the surface (re-cast onto it for the exact point and, for mesh colliders, its UV).
    void Land(Shot s, Collider surface, Ray probe)
    {
        if (!surface.Raycast(probe, out RaycastHit hit, 2f)) return;
        SurfaceInkManager ink = surface.GetComponent<SurfaceInkManager>();
        if (ink == null) return;
        if (s.authoritative) ink.SplatAt(hit, s.splashSize, s.team); // remotes get the shooter's Splat message
        if (s.impactParticles && templates.TryGetValue(s.prefab, out ProjectileVisual t) && t != null)
            InkParticles.Spawn(t.SplashParticles, hit.point, Quaternion.FromToRotation(Vector3.up, hit.normal), s.colour);
    }

    // A trail under its path: every trailSpacing metres, a small unseen drip that falls and splats
    // (the owner's only; the paint reaches everyone else as Splat messages). Drips hurt nobody.
    static void Drip(Shot s, float moved)
    {
        if (s.trailSpacing <= 0f || !s.authoritative) return;
        s.sinceDrip += moved;
        while (s.sinceDrip >= s.trailSpacing)
        {
            s.sinceDrip -= s.trailSpacing;
            Fire(s.prefab, s.position, s.Velocity * DripCarry + Vector3.down * FallSpeed, s.trailSize, s.team,
                 visible: false, authoritative: true, ownerId: s.ownerId, damage: 0f, impactParticles: false);
        }
    }

    const float DripCarry = 0.1f; // share of the shot's velocity a drip keeps
    public const float FallSpeed = 12f; // unseen ink dropping to the surface below (trails, blasts) starts this fast, metres per second

    static void Detonate(Shot s, Vector3 centre, Object exclude)
    {
        if (s.blast != null) InkExplosion.Detonate(centre, s.blast, s.team, s.ownerId, s.source, s.authoritative, s.colour, exclude, s.prefab);
    }

    void Retire(int index)
    {
        Shot s = live[index];
        if (s.visual != null) pools[s.prefab].Release(s.visual);
        s.visual = null;
        live[index] = live[live.Count - 1];
        live.RemoveAt(live.Count - 1);
        spare.Push(s);
    }

    // The prefab's ProjectileVisual (radius, splashes), and a pool of its instances.
    ProjectileVisual Template(GameObject prefab)
    {
        if (templates.TryGetValue(prefab, out ProjectileVisual t)) return t;
        t = prefab.GetComponent<ProjectileVisual>();
        templates[prefab] = t;
        pools[prefab] = new ObjectPool<ProjectileVisual>(
            createFunc: () =>
            {
                visualsCreated++;
                GameObject go = Instantiate(prefab, transform);
                go.SetActive(false);
                return go.GetComponent<ProjectileVisual>();
            },
            actionOnGet: v => v.gameObject.SetActive(true),
            actionOnRelease: v => v.gameObject.SetActive(false),
            actionOnDestroy: v => { if (v != null) Destroy(v.gameObject); },
            collectionCheck: false, defaultCapacity: 32, maxSize: 512);
        return t;
    }

    class HitDistance : IComparer<RaycastHit>
    {
        public int Compare(RaycastHit a, RaycastHit b) => a.distance.CompareTo(b.distance);
    }
}

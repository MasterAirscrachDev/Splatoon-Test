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
// Remote replays (authoritative = false) only fly and splash. Visuals are pooled per prefab; hidden
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
        public Color colour;

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
                            string source = null)
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
        s.damage = damage;
        s.splashSize = splashSize;
        s.team = team;
        s.authoritative = authoritative;
        s.impactParticles = impactParticles;
        s.ownerId = ownerId;
        s.source = source;
        NetGameManager gm = NetGameManager.Instance;
        s.colour = gm == null ? Color.white : team == 1 ? gm.AlphaTeam : gm.BetaTeam;
        s.visual = visible && template != null ? m.pools[prefab].Get() : null;
        if (s.visual != null)
        {
            s.visual.Show(s.colour, splashSize);
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
            Vector3 next = s.position + s.Velocity * h;
            if (Sweep(s, s.position, next)) return true;
            s.position = next;
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
        for (int i = 0; i < n; i++)
        {
            RaycastHit h = hits[i];
            Collider c = h.collider;
            bool overlapping = h.distance <= 0f && h.point == Vector3.zero; // the step started inside it
            Vector3 point = overlapping ? from : h.point;

            SubDevice device = c.GetComponentInParent<SubDevice>();
            if (device != null)
            {
                if (device.Team == s.team) continue;
                if (s.authoritative) device.TakeDamage(s.damage, s.team, s.ownerId);
                Impact?.Invoke(s, point);
                return true;
            }
            PlayerHitbox hitbox = c.GetComponentInParent<PlayerHitbox>();
            if (hitbox != null)
            {
                if (hitbox.Team == s.team) continue; // teammates and the shooter
                if (s.authoritative) hitbox.TakeDamage(s.damage, s.team, s.ownerId, s.source);
                Impact?.Invoke(s, point);
                return true;
            }
            if (c.isTrigger) continue; // volumes don't stop ink

            Land(s, c, overlapping ? new Ray(from - dir * 0.5f, dir) : new Ray(h.point + h.normal * 0.5f, -h.normal));
            Impact?.Invoke(s, point);
            return true;
        }
        return false;
    }

    // Splats the surface (re-cast onto it for the exact point and, for mesh colliders, its UV).
    void Land(Shot s, Collider surface, Ray probe)
    {
        if (!surface.Raycast(probe, out RaycastHit hit, 2f)) return;
        SurfaceInkManager ink = surface.GetComponent<SurfaceInkManager>();
        if (ink == null) return;
        if (s.authoritative) ink.Splat(ink.UVFromHit(hit), s.splashSize, s.team); // remotes get the shooter's Splat message
        if (s.impactParticles && templates.TryGetValue(s.prefab, out ProjectileVisual t) && t != null)
            InkParticles.Spawn(t.SplashParticles, hit.point, Quaternion.FromToRotation(Vector3.up, hit.normal), s.colour);
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

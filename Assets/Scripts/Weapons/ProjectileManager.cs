using System.Collections.Generic;
using UnityEngine;

// Every ink projectile in flight. Shots are plain data, moved each frame in steps of at most
// MaxStep: launch velocity × its speed curve, plus the shooter's carried velocity, plus gravity
// built up through its gravity curve (see Ballistics). The stretch each step covers is swept for
// hits, so fast shots can't skip through anything. Only layers the Projectile layer collides with
// count (the physics matrix: grates let ink through). Enemy hitboxes and subs take damage,
// teammates' are passed through, and the first solid surface stops the shot (splatting it if it's
// inkable).
// Shots with a blast (see InkBlast) explode instead of splatting: on whatever they hit, or in
// mid-air once they've flown their maxDistance. Remote replays (authoritative = false) only fly,
// splash and show their explosions. Seen shots are drawn as fluid ink (InkFluid: the shot, its
// companion blobs, and a splatter where it lands), sized from their splat; hidden ones only paint.
public class ProjectileManager : MonoBehaviour
{
    public class Shot
    {
        public Vector3 position, launch, inherit, fallVelocity;
        public float age;
        public Ballistics ballistics;
        public bool visible;
        public float drawRadius, seed;        // its look (InkFluid)...
        public ShotLook look;                 // ...and the rest of it
        public float radius, damage;
        public int splashSize, team;
        public bool authoritative, splashes;  // splashes: a splatter where it lands (seen shots)
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

    public const float HitRadius = 0.12f; // a shot's, before its radiusScale
    const float MaxStep = 1f / 120f; // longest single movement step
    const float MaxLifetime = 10f;
    const float KillHeight = -10f;   // fell off the level

    // A shot ended by hitting something: where. Fired before the shot is recycled.
    public static event System.Action<Shot, Vector3> Impact;

    // How a shot's flight ended: on a surface, on a target (a player or sub), at its maxDistance in
    // mid-air, on a spawn barrier, too old, or fallen out of the level.
    public enum EndReason { Surface, Target, Air, Barrier, Expired, Fell }
    // Every shot's end (ShotDebug: how long shots really live), with where; fired before it's recycled.
    public static event System.Action<Shot, Vector3, EndReason> Ended;

    static ProjectileManager instance;
    static ProjectileManager Instance => instance != null ? instance : instance = new GameObject("ProjectileManager").AddComponent<ProjectileManager>();

    readonly List<Shot> live = new List<Shot>();
    readonly Stack<Shot> spare = new Stack<Shot>();
    readonly RaycastHit[] hits = new RaycastHit[16];
    static readonly HitDistance ByDistance = new HitDistance();
    int hitMask, shotsCreated;

    public static int LiveCount => instance != null ? instance.live.Count : 0;
    public static IReadOnlyList<Shot> Live => instance != null ? instance.live : (IReadOnlyList<Shot>)System.Array.Empty<Shot>(); // tests
    public static int ShotsCreated => instance != null ? instance.shotsCreated : 0; // tests: pooling
    public static int VisibleCount
    {
        get
        {
            int n = 0;
            if (instance != null) foreach (Shot s in instance.live) if (s.visible) n++;
            return n;
        }
    }

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
    // carried velocity. A seen one is drawn by its `look` (its weapon's: size, stretch and companion
    // blobs; InkFluidSettings' default if none).
    public static Shot Fire(Vector3 origin, Vector3 launch, int splashSize, int team,
                            bool visible = true, bool authoritative = true, ulong ownerId = 0, float damage = 30f,
                            bool splashes = true, Ballistics ballistics = null, Vector3 inherit = default,
                            string source = null, float maxDistance = 0f, InkBlast blast = null,
                            float trailSpacing = 0f, int trailSize = 0, HashSet<Object> hitGroup = null, ShotLook look = null,
                            DamageFalloff falloff = null, float radiusScale = 1f)
    {
        ProjectileManager m = Instance;
        Shot s;
        if (m.spare.Count > 0) s = m.spare.Pop();
        else { s = new Shot(); m.shotsCreated++; }
        s.position = origin;
        s.launch = launch;
        s.inherit = inherit;
        s.fallVelocity = Vector3.zero;
        s.age = 0f;
        s.ballistics = ballistics ?? Ballistics.Default;
        s.radius = HitRadius * radiusScale;
        s.damage = falloff != null ? falloff.damage : damage;
        s.falloff = falloff;
        s.splashSize = splashSize;
        s.team = team;
        s.authoritative = authoritative;
        s.splashes = splashes;
        s.ownerId = ownerId;
        s.source = source;
        s.maxDistance = maxDistance;
        s.travelled = 0f;
        s.blast = blast;
        s.trailSpacing = trailSpacing;
        s.trailSize = trailSize;
        s.sinceDrip = 0f;
        s.hitGroup = hitGroup;
        s.colour = InkFluid.Colour(team);
        s.visible = visible;
        s.look = look ?? InkFluidSettings.Instance.defaultLook;
        if (visible)
        {
            s.drawRadius = InkFluid.Radius(splashSize, s.look.size);
            s.seed = Random.value * 100f;
            InkFluid.Companions(origin, launch, inherit, s.ballistics, s.drawRadius, s.colour, s.look, maxDistance);
        }
        m.live.Add(s);
        return s;
    }

    void Update()
    {
        float dt = Time.deltaTime; // (0 while paused: still drawn)
        for (int i = live.Count - 1; i >= 0; i--) // removal swaps in the last one, already done
        {
            Shot s = live[i];
            if (dt > 0f && Advance(s, dt)) { Retire(i); continue; }
            if (s.visible) InkFluid.DrawShot(s.position, s.Velocity, s.drawRadius, s.age, s.seed, s.colour, s.look.stretch);
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
                End(s, s.position, EndReason.Air);
                return true;
            }
            if (s.age > MaxLifetime) { Ended?.Invoke(s, s.position, EndReason.Expired); return true; }
            if (s.position.y < KillHeight) { Ended?.Invoke(s, s.position, EndReason.Fell); return true; }
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
                End(s, point, EndReason.Target);
                return true;
            }
            PlayerHitbox hitbox = c.GetComponentInParent<PlayerHitbox>();
            if (hitbox != null)
            {
                if (hitbox.Team == s.team || s.damage <= 0f) continue; // teammates and the shooter; drips fall through
                if (s.authoritative && FirstHit(s, hitbox)) hitbox.TakeDamage(s.DamageNow, s.team, s.ownerId, s.source);
                Detonate(s, from + dir * Mathf.Max(0f, h.distance), hitbox); // they took the direct hit
                End(s, point, EndReason.Target);
                return true;
            }
            if (c.isTrigger) continue; // volumes don't stop ink

            if (s.blast != null) Detonate(s, overlapping ? from : h.point + h.normal * 0.1f, null);
            else Land(s, c, overlapping ? new Ray(from - dir * 0.5f, dir) : new Ray(h.point + h.normal * 0.5f, -h.normal));
            End(s, point, EndReason.Surface);
            return true;
        }
        if (barrier)
        {
            // Stopped on the barrier: no ink, no hit inside (a blast still goes off, out here).
            Vector3 at = from + dir * barrierAt;
            pad.Pulse(at);
            Detonate(s, at - dir * 0.1f, null);
            if (s.splashes && s.visible) InkFluid.Splatter(at, (at - pad.Centre).normalized, s.Velocity, s.drawRadius, s.colour);
            End(s, at, EndReason.Barrier);
            return true;
        }
        return false;
    }

    // It hit something (or flew its full distance): both events.
    static void End(Shot s, Vector3 at, EndReason reason)
    {
        Impact?.Invoke(s, at);
        Ended?.Invoke(s, at, reason);
    }

    static bool FirstHit(Shot s, Object target) => s.hitGroup == null || s.hitGroup.Add(target);

    // Splats the surface (re-cast onto it for the exact point and, for mesh colliders, its UV), and
    // splatters there if it's seen (on any surface).
    void Land(Shot s, Collider surface, Ray probe)
    {
        if (!surface.Raycast(probe, out RaycastHit hit, 2f)) return;
        if (s.splashes && s.visible) InkFluid.Splatter(hit.point, hit.normal, s.Velocity, s.drawRadius, s.colour);
        SurfaceInkManager ink = surface.GetComponent<SurfaceInkManager>();
        if (ink != null && s.authoritative) ink.SplatAt(hit, s.splashSize, s.team); // remotes get the shooter's Splat message
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
            Fire(s.position, s.Velocity * DripCarry + Vector3.down * FallSpeed, s.trailSize, s.team,
                 visible: false, authoritative: true, ownerId: s.ownerId, damage: 0f, splashes: false);
        }
    }

    const float DripCarry = 0.1f; // share of the shot's velocity a drip keeps
    public const float FallSpeed = 12f; // unseen ink dropping to the surface below (trails, blasts) starts this fast, metres per second

    static void Detonate(Shot s, Vector3 centre, Object exclude)
    {
        if (s.blast != null) InkExplosion.Detonate(centre, s.blast, s.team, s.ownerId, s.source, s.authoritative, s.colour, exclude, falls: true);
    }

    void Retire(int index)
    {
        Shot s = live[index];
        live[index] = live[live.Count - 1];
        live.RemoveAt(live.Count - 1);
        spare.Push(s);
    }

    class HitDistance : IComparer<RaycastHit>
    {
        public int Compare(RaycastHit a, RaycastHit b) => a.distance.CompareTo(b.distance);
    }
}

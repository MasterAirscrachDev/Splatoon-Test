using System.Collections.Generic;
using UnityEngine;

// Sub weapon: slides along the ground from the thrower's aim, inking a stripe as it goes, glancing
// off walls and following slopes and drops, and clipping enemies it passes through (it keeps
// going). Its fuse runs from the throw, shorter if the button was held first (see PlayerLoadout),
// its light blinking faster as it runs out; then it explodes, inking a wide patch and hurting
// enemies (and their subs) nearby. The owner's copy paints and damages; remote copies slide the
// same way, resynced from the owner's updates (sent at the throw and on every bounce), and show
// the same blast on their own timer. It can't be shot.
public class CurlingBomb : SubDevice
{
    [Header("Slide")]
    [SerializeField] float slideSpeed = 9f;       // as thrown
    [SerializeField] float friction = 1.5f;       // speed lost per second
    [SerializeField] float minSpeed = 2.5f;       // it never slows below this
    [SerializeField] float gravity = 20f;         // off ledges
    [SerializeField] float radius = 0.3f;         // its body: for walls, and for clipping players
    [SerializeField] int trailSplash = 9;         // ink laid along the way
    [SerializeField] float trailSpacing = 0.3f;
    [SerializeField] float passDamage = 15f;      // to each enemy it slides through, once

    [Header("Fuse and blast")]
    [SerializeField] float fuse = 4f;             // from the throw, if it wasn't held
    [SerializeField] float minFuse = 1f;          // holding the button runs it down to this, then it's thrown
    [SerializeField] float blastRadius = 3f;
    [SerializeField] float blastDamage = 45f;
    [SerializeField] int blastSplash = 26;        // splats around the blast
    [SerializeField] GameObject blastParticles;   // splash particles, in the team colour
    [SerializeField] Renderer blinker;            // flashes, faster as the fuse runs out
    [SerializeField] Transform blast;             // sphere that swells and collapses at the blast (hidden until then)
    [SerializeField] Transform body;              // hidden at the blast

    const float GroundProbe = 0.6f;     // how far below its middle to look for ground to ride on
    const float WallLimit = 0.6f;       // surfaces steeper than this (normal's y) are walls
    const float BlastShowTime = 0.4f;   // swelling and collapsing
    public const string Cause = "Curling Bomb";

    static readonly int EmissionId = Shader.PropertyToID("_EmissionColor");
    static readonly int ColourId = Shader.PropertyToID("_Color");

    Vector3 velocity;            // along the ground (or through the air, falling)
    bool launched, grounded, exploded;
    float explodeAt, blastAt, holdStart = -1f;
    Vector3 lastSplat;
    Color colour = Color.white;
    MaterialPropertyBlock block;
    readonly HashSet<PlayerHitbox> clipped = new HashSet<PlayerHitbox>();
    readonly Collider[] overlaps = new Collider[16];

    public override SubType Type => SubType.CurlingBomb;
    public float Fuse => fuse;
    public float MinFuse => minFuse;
    public float SlideSpeed => slideSpeed;
    public float BlastRadius => blastRadius;
    public float BlastDamage => blastDamage;
    public float PassDamage => passDamage;
    public Vector3 Velocity => velocity;
    public bool Launched => launched;
    public bool Grounded => grounded;
    public bool Exploded => exploded;
    public float FuseLeft => exploded ? 0f : launched ? Mathf.Max(0f, explodeAt - Time.time) : holdStart >= 0f ? Mathf.Max(0f, fuse - (Time.time - holdStart)) : fuse;
    public Vector3 Centre => transform.position + transform.up * radius;

    void Awake()
    {
        block = new MaterialPropertyBlock();
        if (blast != null) blast.gameObject.SetActive(false);
    }

    public override void Init(ulong ownerId, int id, int team, bool ownedLocally)
    {
        base.Init(ownerId, id, team, ownedLocally);
        expiresAt = float.MaxValue; // the fuse ends it, not the lifetime
        Tint(team);
    }

    // In the thrower's hand, before Init: the fuse is already running.
    public void Hold(int team)
    {
        holdStart = Time.time;
        Tint(team);
        TintParts(team);
    }

    void Tint(int team)
    {
        NetGameManager gm = NetGameManager.Instance;
        colour = gm == null ? Color.white : team == 2 ? gm.BetaTeam : gm.AlphaTeam;
    }

    // Sets it sliding from where it is (dropping first if it's off the ground), exploding in fuseLeft.
    public void Launch(Vector3 velocity, float fuseLeft)
    {
        launched = true;
        this.velocity = velocity;
        explodeAt = Time.time + fuseLeft;
        lastSplat = transform.position;
        grounded = false;
    }

    // The owner's update (a bounce): snap to it.
    public void Resync(Vector3 position, Vector3 velocity, float fuseLeft)
    {
        transform.position = position;
        Launch(velocity, fuseLeft);
    }

    // The speed and direction to throw one along `aim` (flattened).
    public Vector3 ThrowVelocity(Vector3 aim)
    {
        Vector3 flat = Vector3.ProjectOnPlane(aim, Vector3.up);
        return (flat.sqrMagnitude > 1e-4f ? flat.normalized : Vector3.forward) * slideSpeed;
    }

    protected override void Update()
    {
        if (exploded) { ShowBlast(); return; }
        Blink();
        if (!launched) return;
        if (Time.time >= explodeAt) { Explode(); return; }
        Slide(Time.deltaTime);
        if (authoritative) { LayTrail(); ClipPlayers(); }
        if (transform.position.y < PlayerController.KillHeight) { if (authoritative) Break(); else Remove(); }
    }

    void Blink()
    {
        if (blinker == null) return;
        float left = FuseLeft / fuse;
        float rate = Mathf.Lerp(12f, 2f, left); // flashes per second
        bool on = Mathf.Repeat(Time.time * rate, 1f) < 0.5f;
        blinker.GetPropertyBlock(block);
        block.SetColor(ColourId, colour);
        block.SetColor(EmissionId, colour * (on ? 2f : 0.1f));
        blinker.SetPropertyBlock(block);
    }

    // Moves along the ground: down slopes and off edges under gravity, glancing off walls.
    void Slide(float dt)
    {
        Vector3 flat = Vector3.ProjectOnPlane(velocity, Vector3.up);
        float speed = flat.magnitude;
        if (speed > 0.01f && grounded)
        {
            speed = Mathf.Max(minSpeed, speed - friction * dt);
            flat = flat.normalized * speed;
        }
        float fall = grounded ? 0f : velocity.y - gravity * dt;

        // Walls: sweep the body along its way; bounce off anything steep.
        Vector3 centre = transform.position + Vector3.up * radius;
        Vector3 move = flat * dt;
        if (move.sqrMagnitude > 1e-8f &&
            Physics.SphereCast(centre, radius * 0.9f, move.normalized, out RaycastHit wall, move.magnitude, PhysicsLayers.Environment, QueryTriggerInteraction.Ignore) &&
            Mathf.Abs(wall.normal.y) < WallLimit)
        {
            Vector3 n = Vector3.ProjectOnPlane(wall.normal, Vector3.up).normalized;
            transform.position += move.normalized * Mathf.Max(0f, wall.distance - 0.01f);
            flat = Vector3.Reflect(flat, n);
            move = Vector3.zero;
            velocity = flat + Vector3.up * fall;
            if (authoritative) NetGameManager.Instance?.SendSubSpawn(this); // remotes follow the bounce
        }

        Vector3 next = transform.position + move + Vector3.up * (fall * dt);
        // Ride the ground: find it under where we're going; stick to it unless we're above it and falling.
        grounded = false;
        if (Physics.Raycast(next + Vector3.up * (radius + 0.3f), Vector3.down, out RaycastHit ground, radius + 0.3f + GroundProbe, PhysicsLayers.Environment, QueryTriggerInteraction.Ignore)
            && ground.normal.y >= WallLimit && next.y - ground.point.y <= (fall < 0f ? 0.05f : GroundProbe))
        {
            next.y = ground.point.y;
            grounded = true;
            fall = 0f;
            flat = Vector3.ProjectOnPlane(flat, ground.normal).normalized * flat.magnitude; // along the slope
            flat.y = 0f;
            transform.rotation = Quaternion.LookRotation(flat.sqrMagnitude > 1e-4f ? flat : transform.forward, ground.normal);
        }
        transform.position = next;
        velocity = flat + Vector3.up * fall;
    }

    // A stripe of ink behind it, one splat every trailSpacing along the ground.
    void LayTrail()
    {
        if (!grounded || (transform.position - lastSplat).sqrMagnitude < trailSpacing * trailSpacing) return;
        lastSplat = transform.position;
        Splat(transform.position + Vector3.up * 0.3f, Vector3.down, 0.8f, trailSplash);
    }

    static void Splat(Vector3 from, Vector3 dir, float reach, int size, int team)
    {
        if (!Physics.Raycast(from, dir, out RaycastHit hit, reach, PhysicsLayers.Environment, QueryTriggerInteraction.Ignore)) return;
        SurfaceInkManager ink = hit.collider.GetComponent<SurfaceInkManager>();
        if (ink != null) ink.Splat(ink.UVFromHit(hit), size, team);
    }

    void Splat(Vector3 from, Vector3 dir, float reach, int size) => Splat(from, dir, reach, size, Team);

    // Enemies it slides through take a hit, once each; it carries on.
    void ClipPlayers()
    {
        int n = Physics.OverlapSphereNonAlloc(Centre, radius + 0.1f, overlaps, LayerMask.GetMask("Hitbox"), QueryTriggerInteraction.Collide);
        for (int i = 0; i < n; i++)
        {
            PlayerHitbox hitbox = overlaps[i].GetComponentInParent<PlayerHitbox>();
            if (hitbox == null || hitbox.Team == Team || !clipped.Add(hitbox)) continue;
            hitbox.TakeDamage(passDamage, Team, OwnerId, Cause);
        }
    }

    // Everyone sees the blast; the owner's also paints a wide patch and hurts enemies in range.
    void Explode()
    {
        exploded = true;
        blastAt = Time.time;
        Vector3 centre = Centre;
        if (body != null) body.gameObject.SetActive(false);
        if (blastParticles != null) InkParticles.Spawn(blastParticles, centre, Quaternion.identity, colour);
        if (authoritative)
        {
            // Ink: straight down, a ring around it, and outwards in every direction (nearby walls).
            Splat(centre + Vector3.up * 0.5f, Vector3.down, 2f, blastSplash);
            for (int i = 0; i < 8; i++)
            {
                Vector3 outward = Quaternion.Euler(0f, i * 45f, 0f) * Vector3.forward;
                Splat(centre + outward * (blastRadius * 0.6f) + Vector3.up * 0.5f, Vector3.down, 2.5f, blastSplash);
                Splat(centre, outward, blastRadius, blastSplash);
            }

            // Damage: each enemy player and enemy sub in range, once.
            var struck = new HashSet<Object>();
            foreach (Collider c in Physics.OverlapSphere(centre, blastRadius, LayerMask.GetMask("Hitbox"), QueryTriggerInteraction.Collide))
            {
                PlayerHitbox hitbox = c.GetComponentInParent<PlayerHitbox>();
                if (hitbox != null && hitbox.Team != Team && struck.Add(hitbox)) { hitbox.TakeDamage(blastDamage, Team, OwnerId, Cause); continue; }
                SubDevice sub = c.GetComponentInParent<SubDevice>();
                if (sub != null && sub != this && sub.Team != Team && struck.Add(sub)) sub.TakeDamage(blastDamage, Team, OwnerId);
            }
        }
        Retire(BlastShowTime); // gone from the registry now; the blast plays out first
    }

    // A sphere of ink that swells to most of the blast's reach, then collapses.
    void ShowBlast()
    {
        if (blast == null) return;
        float k = (Time.time - blastAt) / BlastShowTime;
        float size = k < 0.35f ? Mathf.SmoothStep(0.2f, 1f, k / 0.35f) : Mathf.SmoothStep(1f, 0f, (k - 0.35f) / 0.65f);
        blast.gameObject.SetActive(size > 0.01f);
        blast.localScale = Vector3.one * (blastRadius * 1.6f * size);
        Renderer r = blast.GetComponent<Renderer>();
        if (r == null) return;
        r.GetPropertyBlock(block);
        block.SetColor(ColourId, colour);
        r.SetPropertyBlock(block);
    }
}

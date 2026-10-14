using System.Collections.Generic;
using UnityEngine;

// Sub weapon: a bomb, thrown in an arc (its SubThrow) to land where the reticle points, that goes off
// in an ink blast (InkExplosion: full damage in its core, less to the edge; ink on everything
// around, and falling under it), throwing out smaller blobs that ink where they land. What it does on
// reaching something is its contact mode:
//   Stick   (suction bomb): sticks to the level itself (floor, wall or ceiling; it glances off subs and
//           anything else); its fuse runs from then.
//   Burst   (impact bomb): goes off the moment it touches anything, a surface or an enemy.
//   Physics (ink bomb): a rigid body (its Rigidbody and convex collider: a lumpy, semi-triangular
//           shape, so it tumbles and rolls unevenly); its fuse runs from its first knock.
// Its light blinks faster as the fuse runs out. The owner's copy decides what happens: remote copies
// fly (or tumble) the same way and are snapped to the owner's updates (sent as SubSpawn messages at
// the throw, every stick or knock, and the blast); the owner's blast paints and hurts, theirs is the
// look. It can't be shot.
public class ThrownBomb : SubDevice
{
    public enum ContactMode { Stick, Burst, Physics }

    [Header("Bomb")]
    [SerializeField, Tooltip("Which sub it is (the loadout's choice)")] SubType type = SubType.SuctionBomb;
    [SerializeField, Tooltip("Its name: in the kill feed (\"bombed\") and the loadout")] string displayName = "Suction Bomb";
    [SerializeField, Tooltip("Stick: sticks to the level, then its fuse runs. Burst: goes off on touching anything. Physics: a rigid body that tumbles and rolls, its fuse running from its first knock (needs a Rigidbody and collider)")] ContactMode contact = ContactMode.Stick;
    [SerializeField, Tooltip("How it's thrown: top speed, lob angle above the aim, gravity in flight")] SubThrow throwing = new SubThrow();
    [SerializeField, Tooltip("Its body's radius: what it touches things with in flight (metres; Physics uses its collider)")] float radius = 0.15f;
    [SerializeField, Tooltip("Seconds from sticking or first knock until it goes off (Burst doesn't wait)")] float fuse = 2f;

    [Header("Physics")]
    [SerializeField, Tooltip("Physics: random tumble given at the throw (radians per second)")] float throwSpin = 8f;

    [Header("Blast")]
    [SerializeField] InkBlast blast = new InkBlast { radius = 3f, coreRadius = 1.2f, coreDamage = 100f, edgeDamage = 30f, inkReach = 2f, splashSize = 18, fallingDrops = 7, fallSpread = 1.5f };
    [SerializeField, Tooltip("Smaller blobs thrown out from the blast that ink where they land (no damage). 0: none")] int blobs = 0;
    [SerializeField, Tooltip("Their launch speeds, random between these (metres per second)")] Vector2 blobSpeed = new Vector2(5f, 9f);
    [SerializeField, Tooltip("How much they're thrown upwards as well as out (0: flat out, 1: about 45°)")] float blobLift = 0.6f;
    [SerializeField, Tooltip("Each blob's splat size (also how big it's drawn)")] int blobSplat = 8;
    [SerializeField, Tooltip("How the blobs are drawn")] ShotLook blobLook = new ShotLook { companions = 1 };
    [SerializeField, Tooltip("Flashes, faster as the fuse runs out")] Renderer blinker;
    [SerializeField, Tooltip("Hidden at the blast")] Transform body;

    const float BurstWait = 1f;              // a remote Burst copy that touched down waits this long for the owner's blast
    const float KnockGap = 0.1f;             // Physics: knocks closer together than this aren't sent again
    static readonly int EmissionId = Shader.PropertyToID("_EmissionColor");
    static readonly int ColourId = Shader.PropertyToID("_Color");

    Vector3 velocity, surfaceNormal = Vector3.up;
    bool thrown, stuck, armed, exploded, resting;
    float explodeAt, waitingSince = -1f, lastTold = -1f;
    Color colour = Color.white;
    MaterialPropertyBlock block;
    Rigidbody rb;
    readonly Collider[] overlaps = new Collider[16];

    public override SubType Type => type;
    public string DisplayName => displayName;
    public ContactMode Contact => contact;
    public SubThrow Throwing => throwing;
    public InkBlast Blast => blast;
    public float Fuse => fuse;
    public int Blobs => blobs;
    public Vector3 Velocity => rb != null && contact == ContactMode.Physics ? rb.linearVelocity : velocity;
    public Vector3 SurfaceNormal => surfaceNormal;
    public bool Stuck => stuck;
    public bool Resting => resting;
    public bool Settled => stuck || resting;              // stuck to a surface, or come to rest
    public bool Armed => armed;
    public bool Exploded => exploded;
    public int Bounces { get; private set; }             // tests: Physics knocks, Stick glances
    public int BlobsFired { get; private set; }          // tests
    public Vector3 ExplodedAt { get; private set; }      // tests
    public float FuseLeft => exploded ? 0f : armed ? Mathf.Max(0f, explodeAt - Time.time) : fuse;
    public Vector3 Centre => stuck ? transform.position + surfaceNormal * radius : transform.position;

    void Awake()
    {
        block = new MaterialPropertyBlock();
        rb = GetComponent<Rigidbody>();
        if (rb != null) { rb.isKinematic = true; rb.useGravity = false; } // until it's thrown
    }

    public override void Init(ulong ownerId, int id, int team, bool ownedLocally)
    {
        base.Init(ownerId, id, team, ownedLocally);
        expiresAt = float.MaxValue; // its fuse ends it, not the lifetime
        NetGameManager gm = NetGameManager.Instance;
        colour = gm == null ? Color.white : gm.TeamColour(team);
    }

    bool UsesPhysics => contact == ContactMode.Physics && rb != null;

    // Sets it flying from where it is.
    public void Throw(Vector3 velocity)
    {
        thrown = true;
        stuck = resting = false;
        this.velocity = velocity;
        if (UsesPhysics)
        {
            rb.isKinematic = false;
            rb.linearVelocity = velocity;
            rb.angularVelocity = Random.onUnitSphere * throwSpin;
        }
        Face(velocity);
    }

    // The owner's update: where it is and what it's doing (fuse < 0: it's gone off there).
    public void ApplyUpdate(Vector3 position, Vector3 velocity, Vector3 normal, bool settled, float fuseLeft)
    {
        if (exploded) return;
        if (!thrown) Throw(velocity);
        transform.position = position;
        if (fuseLeft < 0f) { Explode(); return; }
        if (fuseLeft > 0f) Arm(fuseLeft);
        if (UsesPhysics)
        {
            rb.position = position;
            rb.linearVelocity = velocity;
            resting = settled;
            return;
        }
        this.velocity = velocity;
        if (settled && contact == ContactMode.Stick) StickTo(position, normal);
        else stuck = false;
    }

    void Arm(float seconds)
    {
        armed = true;
        explodeAt = Time.time + seconds;
    }

    protected override void Update()
    {
        if (exploded || !thrown) return;
        Blink();
        if (armed && Time.time >= explodeAt) { if (authoritative || contact != ContactMode.Burst) Explode(); return; }
        if (waitingSince >= 0f) { if (Time.time - waitingSince > BurstWait) Explode(); return; } // remote Burst, the owner's blast never came
        if (UsesPhysics) WatchRest();
        else if (!stuck) Fly(Time.deltaTime);
        if (transform.position.y < PlayerController.KillHeight) { if (authoritative) Break(); else Remove(); }
    }

    // ── Flight (Stick and Burst) ───────────────────────────────────────────

    void Fly(float dt)
    {
        Vector3 from = transform.position;
        velocity += Vector3.down * (throwing.gravity * dt);
        Vector3 step = velocity * dt;
        if (step.sqrMagnitude < 1e-10f) return;
        // A Burst bomb goes off on an enemy too (the owner's copy decides that).
        if (contact == ContactMode.Burst && authoritative && HitsEnemy(from, step)) { transform.position = from + step; Explode(); return; }
        if (Physics.SphereCast(from, radius, step.normalized, out RaycastHit hit, step.magnitude, PhysicsLayers.Environment, QueryTriggerInteraction.Ignore))
        {
            transform.position = from + step.normalized * Mathf.Max(0f, hit.distance - 0.005f);
            Touch(hit);
            return;
        }
        transform.position = from + step;
        Face(velocity);
    }

    bool HitsEnemy(Vector3 from, Vector3 step)
    {
        int n = Physics.OverlapCapsuleNonAlloc(from, from + step, radius, overlaps, LayerMask.GetMask("Hitbox"), QueryTriggerInteraction.Collide);
        for (int i = 0; i < n; i++)
        {
            PlayerHitbox hitbox = overlaps[i].GetComponentInParent<PlayerHitbox>();
            if (hitbox != null && hitbox.Team != Team) return true;
            SubDevice sub = overlaps[i].GetComponentInParent<SubDevice>();
            if (sub != null && sub != this && sub.Team != Team) return true;
        }
        return false;
    }

    // The level itself: the map's geometry (on its layers, or inkable), not a prop, sub, player or
    // anything else that moves.
    static int levelLayers = -1;
    static bool IsLevel(Collider c)
    {
        if (levelLayers < 0) levelLayers = LayerMask.GetMask("InkSurface", "Map", "MapGrate");
        bool map = (levelLayers & (1 << c.gameObject.layer)) != 0 || c.GetComponent<SurfaceInkManager>() != null;
        return map && c.attachedRigidbody == null && c.GetComponentInParent<SubDevice>() == null && c.GetComponentInParent<PlayerController>() == null;
    }

    // It reached a surface: stick (to the level; off anything else it glances away) or burst.
    void Touch(RaycastHit hit)
    {
        if (contact == ContactMode.Burst)
        {
            if (authoritative) Explode();
            else { velocity = Vector3.zero; waitingSince = Time.time; }
            return;
        }
        if (!IsLevel(hit.collider))
        {
            Bounces++;
            velocity = Vector3.Reflect(velocity, hit.normal) * 0.4f;
            return;
        }
        if (!authoritative) { velocity = Vector3.zero; return; } // where it sticks is the owner's word
        StickTo(hit.point, hit.normal);
        Arm(fuse);
        Tell();
    }

    void StickTo(Vector3 point, Vector3 normal)
    {
        stuck = true;
        velocity = Vector3.zero;
        surfaceNormal = normal;
        Vector3 forward = Vector3.ProjectOnPlane(transform.forward, normal);
        if (forward.sqrMagnitude < 1e-4f) forward = Vector3.ProjectOnPlane(Vector3.forward, normal);
        if (forward.sqrMagnitude < 1e-4f) forward = Vector3.ProjectOnPlane(Vector3.right, normal);
        transform.SetPositionAndRotation(point, Quaternion.LookRotation(forward, normal)); // upright off the surface
    }

    // ── Physics (the ink bomb) ─────────────────────────────────────────────

    void FixedUpdate()
    {
        if (UsesPhysics && thrown && !exploded && !rb.isKinematic) rb.AddForce(Vector3.down * throwing.gravity, ForceMode.Acceleration); // its own gravity, as its throw's
    }

    // Each knock: the first lights its fuse; the owner tells the others where it is now.
    void OnCollisionEnter(Collision c)
    {
        if (!UsesPhysics || exploded || !thrown) return;
        Bounces++;
        if (c.contactCount > 0) surfaceNormal = c.GetContact(0).normal;
        if (!authoritative) return;
        if (!armed) Arm(fuse);
        if (Time.time - lastTold >= KnockGap) Tell();
    }

    void WatchRest()
    {
        bool still = rb.IsSleeping() || rb.linearVelocity.sqrMagnitude < 0.01f && rb.angularVelocity.sqrMagnitude < 0.04f && Bounces > 0;
        if (still && !resting && authoritative) { resting = true; Tell(); }
        else if (!still) resting = false;
    }

    // ── Blast ──────────────────────────────────────────────────────────────

    void Explode()
    {
        if (exploded) return;
        exploded = true;
        ExplodedAt = Centre;
        if (authoritative) Tell(); // remotes go off here too
        if (UsesPhysics) rb.isKinematic = true;
        if (body != null) body.gameObject.SetActive(false);
        InkExplosion.Detonate(ExplodedAt, blast, Team, OwnerId, displayName, authoritative, colour, falls: true);
        ThrowBlobs();
        Retire(0.5f);
    }

    // Smaller blobs out in every direction (up and away from the surface it's on), inking where they
    // land. Everyone throws their own (the owner's paint is what counts).
    void ThrowBlobs()
    {
        Vector3 up = stuck ? surfaceNormal : Vector3.up;
        for (int i = 0; i < blobs; i++)
        {
            Vector3 out_ = Vector3.ProjectOnPlane(Random.onUnitSphere, up);
            if (out_.sqrMagnitude < 1e-4f) out_ = Vector3.ProjectOnPlane(Vector3.forward, up);
            Vector3 dir = (out_.normalized + up * (blobLift * Random.Range(0.6f, 1.2f))).normalized;
            ProjectileManager.Fire(ExplodedAt + up * 0.2f, dir * Random.Range(blobSpeed.x, blobSpeed.y), blobSplat, Team, true, authoritative, OwnerId, 0f,
                                   source: displayName, look: blobLook);
            BlobsFired++;
        }
    }

    void Tell()
    {
        lastTold = Time.time;
        NetGameManager.Instance?.SendSubSpawn(this);
    }

    void Blink()
    {
        if (blinker == null) return;
        float left = armed ? FuseLeft / Mathf.Max(fuse, 0.01f) : 1f;
        float rate = Mathf.Lerp(12f, 2f, left); // flashes per second
        bool on = Mathf.Repeat(Time.time * rate, 1f) < 0.5f;
        blinker.GetPropertyBlock(block);
        block.SetColor(ColourId, colour);
        block.SetColor(EmissionId, colour * (on ? 2f : 0.1f));
        blinker.SetPropertyBlock(block);
    }

    void Face(Vector3 dir)
    {
        if (contact != ContactMode.Burst || dir.sqrMagnitude < 1e-4f) return; // (a suction bomb flies as thrown; the physics one tumbles)
        Vector3 flat = Vector3.ProjectOnPlane(dir, Vector3.up);
        if (flat.sqrMagnitude > 1e-4f) transform.rotation = Quaternion.LookRotation(flat, Vector3.up);
    }

    // Where one thrown from `from` at `velocity` flies, up to what it first reaches (or `seconds`).
    // Returns whether it reaches something (the path's last point is where).
    public bool PredictPath(Vector3 from, Vector3 velocity, float seconds, List<Vector3> path, float dt = 1f / 30f)
    {
        path.Clear();
        path.Add(from);
        Vector3 position = from;
        for (float t = 0f; t < seconds; t += dt)
        {
            velocity += Vector3.down * (throwing.gravity * dt);
            Vector3 step = velocity * dt;
            if (Physics.SphereCast(position, radius, step.normalized, out RaycastHit hit, step.magnitude, PhysicsLayers.Environment, QueryTriggerInteraction.Ignore))
            {
                path.Add(position + step.normalized * hit.distance);
                return true;
            }
            position += step;
            path.Add(position);
        }
        return false;
    }
}

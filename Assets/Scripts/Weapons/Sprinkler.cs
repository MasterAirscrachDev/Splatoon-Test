using UnityEngine;

// Sub weapon: thrown in an arc, sticks to the first surface it hits and spins, spraying ink from
// two opposite nozzles. It winds down over its life (from landing): slower spin, weaker shots,
// longer gaps. Remote copies fly the same arc, snap to the owner's landing, and spray visual-only
// shots on the same schedule (the owner's real shots paint and damage, and their splats sync).
public class Sprinkler : SubDevice
{
    [SerializeField] GameObject projectilePrefab;
    [SerializeField] Transform head;                  // spins about the surface normal; nozzles on its local ±Z
    [SerializeField] float gravity = 20f;             // during the throw
    [SerializeField] float spinStart = 540f, spinEnd = 90f;              // degrees per second
    [SerializeField] float shotSpeedStart = 9f, shotSpeedEnd = 3f;
    [SerializeField] float fireIntervalStart = 0.12f, fireIntervalEnd = 0.45f;
    [SerializeField] float shotLift = 0.35f;          // share of each shot aimed away from the surface
    [SerializeField] int splashSize = 8;
    [SerializeField] float shotDamage = 12f;

    Vector3 velocity;
    bool landed;
    float landedAt, nextShot;

    public override SubType Type => SubType.Sprinkler;
    public bool Landed => landed;
    public Vector3 Velocity => velocity;
    public int ShotsFired { get; private set; }

    // 0 at landing, 1 at the end of its life.
    public float Wind => landed ? Mathf.Clamp01((Time.time - landedAt) / lifetime) : 0f;
    public float SpinSpeed => Mathf.Lerp(spinStart, spinEnd, Wind);
    public float ShotSpeed => Mathf.Lerp(shotSpeedStart, shotSpeedEnd, Wind);
    public float FireInterval => Mathf.Lerp(fireIntervalStart, fireIntervalEnd, Wind);

    public override void Init(ulong ownerId, int id, int team, bool ownedLocally)
    {
        base.Init(ownerId, id, team, ownedLocally);
        expiresAt = float.MaxValue; // the lifetime starts on landing
    }

    public void Throw(Vector3 velocity)
    {
        this.velocity = velocity;
        landed = false;
    }

    // Sticks flat against the surface, upright along its normal.
    public void Land(Vector3 point, Vector3 normal)
    {
        landed = true;
        velocity = Vector3.zero;
        transform.SetPositionAndRotation(point, Quaternion.FromToRotation(Vector3.up, normal));
        landedAt = Time.time;
        expiresAt = landedAt + lifetime;
        nextShot = Time.time;
    }

    protected override void Update()
    {
        base.Update();
        if (broken) return;
        if (!landed) { Fly(); return; }

        if (head != null) head.Rotate(0f, SpinSpeed * Time.deltaTime, 0f, Space.Self);
        if (Time.time >= nextShot)
        {
            nextShot = Time.time + FireInterval;
            Fire();
        }
    }

    void Fly()
    {
        if (velocity == Vector3.zero) return; // remote copy, waiting for the owner's landing
        Vector3 step = velocity * Time.deltaTime;
        velocity += Vector3.down * gravity * Time.deltaTime;
        if (Physics.SphereCast(transform.position, 0.15f, step.normalized, out RaycastHit hit, step.magnitude, PhysicsLayers.Environment, QueryTriggerInteraction.Ignore))
        {
            if (authoritative)
            {
                Land(hit.point, hit.normal);
                NetGameManager.Instance?.SendSubSpawn(this); // tells remotes where it stuck
            }
            else
            {
                transform.position = hit.point;
                velocity = Vector3.zero;
            }
            return;
        }
        transform.position += step;
        if (transform.position.y < -10f && authoritative) Break(); // thrown off the level
    }

    // One shot from each nozzle, along the surface and a little away from it.
    void Fire()
    {
        if (projectilePrefab == null || head == null) return;
        for (int side = -1; side <= 1; side += 2)
        {
            Vector3 along = head.forward * side;
            Vector3 dir = (along + transform.up * shotLift).normalized;
            Vector3 from = head.position + along * 0.25f;
            ProjectilePool.Get(projectilePrefab, from, Quaternion.LookRotation(dir))
                          .Setup(dir * ShotSpeed, splashSize, Team, true, authoritative, OwnerId, shotDamage);
        }
        ShotsFired++;
    }
}

using System.Collections.Generic;
using UnityEngine;

// Sub weapon: thrown in an arc, sticks to the first surface it hits and spins, spraying ink from
// two opposite nozzles. It winds down over its life (from landing): slower spin, weaker shots,
// longer gaps. Remote copies fly the same arc, snap to the owner's landing, and spray visual-only
// shots on the same schedule (the owner's real shots paint and damage, and their splats sync).
public class Sprinkler : SubDevice
{
    [SerializeField, Tooltip("The spinning part: it turns about the surface it's stuck to, with nozzles on its local ±Z")] Transform head;
    [SerializeField, Tooltip("How it's thrown: speed, lob angle above the aim, gravity in flight")] SubThrow throwing = new SubThrow { speed = 16f, angle = 25f, gravity = 20f };
    [SerializeField, Tooltip("Spin speed when it lands and at the end of its life (degrees per second): it winds down between them")] float spinStart = 540f, spinEnd = 90f;
    [SerializeField, Tooltip("Shot speed when it lands and at the end of its life (metres per second)")] float shotSpeedStart = 9f, shotSpeedEnd = 3f;
    [SerializeField, Tooltip("Seconds between shots when it lands and at the end of its life")] float fireIntervalStart = 0.12f, fireIntervalEnd = 0.45f;
    [SerializeField, Tooltip("How much each shot is aimed away from the surface it's stuck to (0: along it)")] float shotLift = 0.35f;
    [SerializeField, Tooltip("Each shot's splat size (also how big it's drawn)")] int splashSize = 8;
    [SerializeField, Tooltip("Each shot's damage")] float shotDamage = 12f;
    [SerializeField, Tooltip("How its shots are drawn")] ShotLook shotLook = new ShotLook();

    Vector3 velocity;
    bool landed;
    float landedAt, nextShot;

    public override SubType Type => SubType.Sprinkler;
    public bool Landed => landed;
    public SubThrow Throwing => throwing;
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

    // Where one thrown from `from` at `velocity` flies, as Fly moves it, up to where it would stick
    // (or `seconds`). Returns whether it hits something (the path's last point is where).
    public bool PredictPath(Vector3 from, Vector3 velocity, float seconds, List<Vector3> path, float dt = 1f / 30f)
    {
        path.Clear();
        path.Add(from);
        Vector3 position = from;
        for (float t = 0f; t < seconds; t += dt)
        {
            Vector3 step = velocity * dt;
            velocity += Vector3.down * throwing.gravity * dt;
            if (Physics.SphereCast(position, 0.15f, step.normalized, out RaycastHit hit, step.magnitude, PhysicsLayers.Environment, QueryTriggerInteraction.Ignore))
            {
                path.Add(hit.point);
                return true;
            }
            position += step;
            path.Add(position);
        }
        return false;
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
        velocity += Vector3.down * throwing.gravity * Time.deltaTime;
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
        if (head == null) return;
        for (int side = -1; side <= 1; side += 2)
        {
            Vector3 along = head.forward * side;
            Vector3 dir = (along + transform.up * shotLift).normalized;
            Vector3 from = head.position + along * 0.25f;
            ProjectileManager.Fire(from, dir * ShotSpeed, splashSize, Team, true, authoritative, OwnerId, shotDamage, source: "Sprinkler", look: shotLook);
        }
        ShotsFired++;
    }
}

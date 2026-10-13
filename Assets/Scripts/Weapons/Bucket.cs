using System.Collections.Generic;
using UnityEngine;

// Bucket (a slosher): each press swings the bucket forward and throws its ink out in a stream, like
// the Roller's air flick (a line of droplets, nearest to furthest) but more direct: a flatter, faster
// throw that follows the view all the way up and down. The stream's front is one big blob, the
// furthest and fastest, that splats wider, is easier to hit with and hits hardest; the droplets
// behind it ink the line and catch what it misses. An enemy takes one hit per slosh (whichever
// reaches them first: the blob, out in front). Holding attack keeps sloshing. Remote copies show the
// swing and replay the stream (the blob drawn big); paint and damage are the owner's.
public class Bucket : Weapon
{
    [Header("Slosh")]
    [SerializeField] float sloshRate = 0.6f;          // seconds between sloshes
    [SerializeField] float windup = 0.12f;            // swing back before the ink leaves
    [SerializeField] float followThrough = 0.2f;      // swing on after it does
    [SerializeField] float sloshInkCost = 70f;        // x0.001 of a full tank
    [SerializeField] GameObject projectile;
    [SerializeField] Ballistics ballistics = new Ballistics();
    [SerializeField] float sloshPace = 2f;            // the ink flies its path this many times as fast (speeds below are before it)
    [SerializeField] float aimInfluence = 1f;         // how much looking up/down tilts the throw (1: as much as the view)
    [SerializeField] Vector2 sloshPitchLimits = new Vector2(-60f, 70f); // degrees (down, up) the throw can be aimed
    [SerializeField] float sloshPitch = 8f;           // degrees up from the view (the roller's air flick is 20)
    [SerializeField] float spread = 2f;               // degrees of wobble, left and right...
    [SerializeField] float sway = 2.5f;               // ...and of the line's bend side to side (a different one each slosh): not dead straight

    [Header("Stream: the droplets behind the blob")]
    [SerializeField] int droplets = 6;
    [SerializeField] Vector2 dropletSpeed = new Vector2(6f, 11f);    // nearest to furthest, so they land along a line
    [SerializeField] float dropletScale = 1.3f;       // how big they look, x a shot's size for their splat (about 0.45m)
    [SerializeField] DamageFalloff dropletDamage = new DamageFalloff(35f, DamageFalloff.HoldThenFall(0.1f, 0.5f, 0.5f));
    [SerializeField] int dropletSplat = 10;

    [Header("Blob: the stream's front")]
    [SerializeField] float blobSpeed = 12f;           // a little faster than the furthest droplet: out in front (about the air flick's reach)
    [SerializeField] float blobScale = 1.9f;          // how big it looks (about 1.2m: with its bigger splat size, over twice a droplet)
    [SerializeField] float blobHitScale = 1.8f;       // x a shot's hit radius: a bigger target to land
    [SerializeField] DamageFalloff blobDamage = new DamageFalloff(90f, DamageFalloff.HoldThenFall(0.15f, 0.6f, 0.65f)); // well over a droplet's
    [SerializeField] int blobSplat = 18;

    [Header("Model")]
    [SerializeField] float idlePitch = 0f;            // hanging from the hand
    [SerializeField] float swingBack = 35f;           // degrees back at the top of the windup...
    [SerializeField] float swingForward = -110f;      // ...and tipped forward as the ink leaves
    [SerializeField] float poseSpeed = 14f;

    enum State { Idle, Slosh }

    ControlLayer input;
    State state;
    bool local, wantSlosh;
    float sloshStart = -10f, released = -20f;
    CharacterController body;
    Vector3 homePos;
    float pitch;
    Ballistics paced;
    HashSet<Object> sloshHits;

    public bool ScriptedFire { get; set; } // tests: hold the trigger
    public int Sloshes { get; private set; }
    public Vector3[] LastSlosh { get; private set; } // launch velocities of the last slosh, the blob last (tests)
    public DamageFalloff DropletDamage => dropletDamage;
    public DamageFalloff BlobDamage => blobDamage;
    public int Droplets => droplets;
    public int BlobSplat => blobSplat;
    public int DropletSplat => dropletSplat;
    public float BlobScale => blobScale;
    public float DropletScale => dropletScale;
    public float BlobHitScale => blobHitScale;
    public float SloshRate => sloshRate;
    public float SloshInkCost => sloshInkCost;
    public float SloshPitch => sloshPitch;
    public float Spread => spread;
    public float Sway => sway;
    public bool Sloshing => state == State.Slosh;
    public float PitchNow => pitch;
    public string Source => DisplayName;

    public override void Equip(PlayerController owner)
    {
        base.Equip(owner);
        local = owner.IsLocalPlayer;
        enabled = true; // remote copies still swing
        WeaponCameraAim aim = GetComponent<WeaponCameraAim>();
        if (aim != null) aim.enabled = false; // posed by the bucket itself
    }

    void Start()
    {
        input = new ControlLayer();
        input.Enable();
        homePos = transform.localPosition;
        pitch = idlePitch;
        paced = ballistics.Faster(sloshPace);
        if (Owner != null) body = Owner.GetComponent<CharacterController>();
    }

    protected override void OnDestroy()
    {
        base.OnDestroy();
        input?.Disable();
        input?.Dispose();
    }

    void Update()
    {
        PlayerController player = Owner;
        if (player == null) return;
        if (local) Think(player);
        else if (state == State.Slosh && Time.time - sloshStart >= windup + followThrough) state = State.Idle; // their swing played out
    }

    void Think(PlayerController player)
    {
        bool hardware = !player.IsTestPlayer && player.ScriptedInput == null && !InputGate.Blocked && input.Weapon.Attack.ReadValue<float>() != 0;
        bool held = !InputGate.MatchLocked && !player.IsDead && (ScriptedFire || hardware);
        player.AttackHeld = held;
        bool pressed = held && !player.IsSquid;
        wantSlosh = pressed; // holding keeps sloshing
        if (player.IsSquid || player.IsDead || InputGate.MatchLocked) state = State.Idle;

        // Goes as soon as it can: retried each frame while held (the kid form may still be coming up).
        if (wantSlosh && state != State.Slosh && Time.time - sloshStart >= sloshRate && player.ConsumeInk(sloshInkCost * 0.001f))
        {
            player.MarkAiming(); // face the view for the swing
            state = State.Slosh;
            sloshStart = Time.time;
            sloshHits = new HashSet<Object>();
            Sloshes++;
        }
        if (state == State.Slosh)
        {
            player.MarkAiming();
            float t = Time.time - sloshStart;
            if (t >= windup && released != sloshStart) Release(player);
            if (t >= windup + followThrough) state = State.Idle;
        }
    }

    // The ink leaves from in front of us at the hand's height: the droplets slowest to fastest, then
    // the blob, all along one line where we look.
    void Release(PlayerController player)
    {
        released = sloshStart;
        Vector3 flat = Flat(player);
        Vector3 origin = Pivot() + flat * 0.6f + Vector3.up * 0.25f;
        float aimUp = -player.RaisedAimPitch * aimInfluence;
        Vector3 inherit = body != null ? body.velocity : Vector3.zero;
        inherit.y = 0f; // running carries into the stream; jumping doesn't
        float facing = player.transform.eulerAngles.y;
        int n = droplets + 1;
        float phase = Random.Range(0f, Mathf.PI * 2f), bends = Random.Range(1f, 2f); // this slosh's sway
        var launches = new Vector3[n];
        var velocities = new float[n * 3];
        var sizes = new int[n];
        var visible = new bool[n];
        for (int i = 0; i < n; i++)
        {
            bool blob = i == n - 1;
            float k = droplets > 1 ? Mathf.Min(i, droplets - 1) / (droplets - 1f) : 1f;
            float speed = blob ? blobSpeed : Mathf.Lerp(dropletSpeed.x, dropletSpeed.y, k);
            float yawOff = blob ? 0f : Random.Range(-spread, spread) + sway * Mathf.Sin(k * Mathf.PI * bends + phase);
            float up = Mathf.Clamp(sloshPitch + (blob ? 0f : Random.Range(-1.5f, 1.5f)) + aimUp, sloshPitchLimits.x, sloshPitchLimits.y);
            Vector3 launch = Quaternion.Euler(-up, facing + yawOff, 0f) * Vector3.forward * (speed * sloshPace);
            launches[i] = launch;
            velocities[i * 3] = launch.x; velocities[i * 3 + 1] = launch.y; velocities[i * 3 + 2] = launch.z;
            sizes[i] = blob ? blobSplat : dropletSplat;
            visible[i] = true;
            Fire(origin, launch, inherit, sizes[i], player.Team, player.OwnerId, true, blob, sloshHits);
        }
        LastSlosh = launches;
        NetGameManager.Instance?.BroadcastShots(player, origin, inherit, velocities, sizes, visible);
    }

    void Fire(Vector3 from, Vector3 launch, Vector3 inherit, int size, int team, ulong ownerId, bool authoritative, bool blob, HashSet<Object> hits)
    {
        ProjectileManager.Fire(projectile, from, launch, size, team, true, authoritative, ownerId, 0f,
                               ballistics: paced ?? ballistics, falloff: authoritative ? (blob ? blobDamage : dropletDamage) : null, inherit: inherit,
                               source: Source, hitGroup: hits, visualScale: blob ? blobScale : dropletScale, radiusScale: blob ? blobHitScale : 1f);
    }

    // Their slosh, visual-only here, the swing played from where the ink leaves. The blob is the
    // biggest splat (the stream's front).
    public override void Replay(ProjectileSpawnData d)
    {
        if (projectile == null || d.velocities == null || d.splashSizes == null) return;
        Vector3 inherit = d.inherit != null ? (Vector3)d.inherit : Vector3.zero;
        int n = Mathf.Min(d.splashSizes.Length, d.velocities.Length / 3);
        if (!local && n > 0)
        {
            state = State.Slosh;
            sloshStart = Time.time - windup;
        }
        for (int i = 0; i < n; i++)
            Fire(d.origin, new Vector3(d.velocities[i * 3], d.velocities[i * 3 + 1], d.velocities[i * 3 + 2]), inherit, d.splashSizes[i], d.team,
                 d.shooterSteamId, false, d.splashSizes[i] >= blobSplat, null);
    }

    void LateUpdate()
    {
        PlayerController player = Owner;
        if (player == null) return;
        float target = idlePitch;
        bool snap = false;
        if (state == State.Slosh) // back over the windup, then tipped forward through the release
        {
            float t = Time.time - sloshStart;
            target = t < windup ? Mathf.Lerp(idlePitch, swingBack, t / windup)
                                : Mathf.Lerp(swingBack, swingForward, Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((t - windup) / (followThrough * 0.6f))));
            snap = true;
        }
        float ease = 1f - Mathf.Exp(-poseSpeed * Time.deltaTime);
        pitch = snap ? target : Mathf.Lerp(pitch, target, ease);
        transform.localPosition = homePos;
        transform.rotation = Quaternion.Euler(pitch, BodyYaw(player), 0f); // along the body (which faces the view while sloshing)
    }

    Vector3 Pivot() => transform.parent != null ? transform.parent.TransformPoint(homePos) : transform.position;
    float BodyYaw(PlayerController player) => transform.parent != null ? transform.parent.eulerAngles.y : player.transform.eulerAngles.y;
    static Vector3 Flat(PlayerController player)
    {
        Vector3 f = player.transform.forward;
        f.y = 0f;
        return f.sqrMagnitude > 1e-6f ? f.normalized : Vector3.forward;
    }
}

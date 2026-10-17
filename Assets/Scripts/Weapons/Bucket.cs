using System.Collections.Generic;
using UnityEngine;

// Bucket (a slosher): each press swings the bucket forward and throws its ink out in a stream, like
// the Roller's air flick (a line of droplets, nearest to furthest) but more direct: a flatter, faster
// throw that follows the view all the way up and down. The stream's front is one big blob, the
// furthest and fastest, that splats wider, is easier to hit with and hits hardest; the droplets
// behind it ink the line and catch what it misses. An enemy takes one hit per slosh (whichever
// reaches them first: the blob, out in front). With several streams (the Triplesplash), each slosh
// throws them fanned out side by side, each with its own blob, and an enemy can take a hit from each.
// Holding attack keeps sloshing. Remote copies show the
// swing and replay the stream (the blob drawn big); paint and damage are the owner's.
public class Bucket : Weapon
{
    [Header("Slosh")]
    [SerializeField, Tooltip("Seconds between sloshes")] float sloshRate = 0.6f;
    [SerializeField, Tooltip("Swing back before the ink leaves")] float windup = 0.12f;
    [SerializeField, Tooltip("Swing on after it does")] float followThrough = 0.2f;
    [SerializeField, Tooltip("Ink per use, in thousandths of a full tank")] float sloshInkCost = 70f;
    [SerializeField] Ballistics ballistics = new Ballistics();
    [SerializeField, Tooltip("The ink flies its whole path this many times as fast (the speeds below are before it): snappy, landing where it would")] float sloshPace = 2f;
    [SerializeField, Tooltip("How much looking up/down tilts the throw (1: as much as the view)")] float aimInfluence = 1f;
    [SerializeField, Tooltip("Degrees (down, up) the throw can be aimed")] Vector2 sloshPitchLimits = new Vector2(-60f, 70f);
    [SerializeField, Tooltip("How far above the view the slosh is thrown (degrees; the roller's air flick is 20)")] float sloshPitch = 8f;
    [SerializeField, Tooltip("Random left-right wobble on each droplet (degrees, ±)")] float spread = 2f;
    [SerializeField, Tooltip("How far the whole line bends side to side (degrees): a different gentle curve each slosh, so it's not dead straight")] float sway = 2.5f;
    [SerializeField, Tooltip("Streams per slosh, fanned out side by side: each a whole stream with its own blob (an enemy can take a hit from each)")] int streams = 1;
    [SerializeField, Tooltip("How wide the streams fan out, the outer two apart (degrees)")] float streamFan = 0f;

    [Header("Stream: the droplets behind the blob")]
    [SerializeField, Tooltip("Droplets in the stream behind the blob")] int droplets = 6;
    [SerializeField, Tooltip("Launch speeds from the nearest droplet to the furthest (before Slosh Pace), so they land along a line")] Vector2 dropletSpeed = new Vector2(6f, 11f);
    [SerializeField] DamageFalloff dropletDamage = new DamageFalloff(35f, DamageFalloff.HoldThenFall(0.1f, 0.5f, 0.5f));
    [SerializeField, Tooltip("Each droplet's splat size (also how big it's drawn)")] int dropletSplat = 10;
    [SerializeField, Tooltip("How the droplets are drawn")] ShotLook dropletLook = new ShotLook { companions = 3, upDownSpread = 1f, sideSpread = 3f, speedSpread = 0.1f };

    [Header("Blob: the stream's front")]
    [SerializeField, Tooltip("A little faster than the furthest droplet: out in front (about the air flick's reach)")] float blobSpeed = 12f;
    [SerializeField, Tooltip("How the blob is drawn (its bigger splat already draws it bigger than a droplet)")] ShotLook blobLook = new ShotLook { size = 1.27f, companions = 3, upDownSpread = 1f, sideSpread = 3f, speedSpread = 0.1f };
    [SerializeField, Tooltip("Its hit radius, x a normal shot's: easier to land")] float blobHitScale = 1.8f;
    [SerializeField, Tooltip("Well over a droplet's")] DamageFalloff blobDamage = new DamageFalloff(90f, DamageFalloff.HoldThenFall(0.15f, 0.6f, 0.65f));
    [SerializeField, Tooltip("Its splat size (also how big it's drawn)")] int blobSplat = 18;

    [Header("Model")]
    [SerializeField, Tooltip("Hanging from the hand")] float idlePitch = 0f;
    [SerializeField, Tooltip("How far it swings back at the top of the windup (degrees)")] float swingBack = 35f;
    [SerializeField, Tooltip("How far it tips forward as the ink leaves (degrees)")] float swingForward = -110f;
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
    public Vector3[] LastSlosh { get; private set; } // launch velocities of the last slosh, stream by stream, each one's blob last (tests)
    public DamageFalloff DropletDamage => dropletDamage;
    public DamageFalloff BlobDamage => blobDamage;
    public int Droplets => droplets;
    public int BlobSplat => blobSplat;
    public int DropletSplat => dropletSplat;
    public ShotLook BlobLook => blobLook;
    public float BlobHitScale => blobHitScale;
    public float SloshRate => sloshRate;
    public float SloshInkCost => sloshInkCost;
    public float SloshPitch => sloshPitch;
    public float Spread => spread;
    public float Sway => sway;
    public int Streams => streams;
    public float StreamFan => streamFan;
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
        bool held = !InputGate.GameLocked && !player.ActionLocked && !player.IsDead && (ScriptedFire || hardware);
        player.AttackHeld = held;
        bool pressed = held && !player.IsSquid;
        wantSlosh = pressed; // holding keeps sloshing
        if (player.IsSquid || player.IsDead || InputGate.GameLocked) state = State.Idle;

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

    // The ink leaves from in front of us at the hand's height: per stream, the droplets slowest to
    // fastest, then the blob, all along one line where we look (fanned apart for several streams).
    void Release(PlayerController player)
    {
        released = sloshStart;
        Vector3 flat = Flat(player);
        Vector3 origin = Pivot() + flat * 0.6f + Vector3.up * 0.25f;
        float aimUp = -player.RaisedAimPitch * aimInfluence;
        Vector3 inherit = body != null ? body.velocity : Vector3.zero;
        inherit.y = 0f; // running carries into the stream; jumping doesn't
        float facing = player.transform.eulerAngles.y;
        int n = droplets + 1, count = Mathf.Max(1, streams);
        var launches = new Vector3[n * count];
        var velocities = new float[n * count * 3];
        var sizes = new int[n * count];
        var visible = new bool[n * count];
        for (int s = 0; s < count; s++)
        {
            float streamYaw = count > 1 ? Mathf.Lerp(-streamFan, streamFan, s / (count - 1f)) * 0.5f : 0f;
            HashSet<Object> hits = s == 0 ? sloshHits : new HashSet<Object>(); // one hit per stream
            float phase = Random.Range(0f, Mathf.PI * 2f), bends = Random.Range(1f, 2f); // this stream's sway
            for (int i = 0; i < n; i++)
            {
                int j = s * n + i;
                bool blob = i == n - 1;
                float k = droplets > 1 ? Mathf.Min(i, droplets - 1) / (droplets - 1f) : 1f;
                float speed = blob ? blobSpeed : Mathf.Lerp(dropletSpeed.x, dropletSpeed.y, k);
                float yawOff = streamYaw + (blob ? 0f : Random.Range(-spread, spread) + sway * Mathf.Sin(k * Mathf.PI * bends + phase));
                float up = Mathf.Clamp(sloshPitch + (blob ? 0f : Random.Range(-1.5f, 1.5f)) + aimUp, sloshPitchLimits.x, sloshPitchLimits.y);
                Vector3 launch = Quaternion.Euler(-up, facing + yawOff, 0f) * Vector3.forward * (speed * sloshPace);
                launches[j] = launch;
                velocities[j * 3] = launch.x; velocities[j * 3 + 1] = launch.y; velocities[j * 3 + 2] = launch.z;
                sizes[j] = blob ? blobSplat : dropletSplat;
                visible[j] = true;
                Fire(origin, launch, inherit, sizes[j], player.Team, player.OwnerId, true, blob, hits);
            }
        }
        LastSlosh = launches;
        NetGameManager.Instance?.BroadcastShots(player, origin, inherit, velocities, sizes, visible);
    }

    void Fire(Vector3 from, Vector3 launch, Vector3 inherit, int size, int team, ulong ownerId, bool authoritative, bool blob, HashSet<Object> hits)
    {
        ProjectileManager.Fire(from, launch, size, team, true, authoritative, ownerId, 0f,
                               ballistics: paced ?? ballistics, falloff: authoritative ? (blob ? blobDamage : dropletDamage) : null, inherit: inherit,
                               source: Source, hitGroup: hits, look: blob ? blobLook : dropletLook, radiusScale: blob ? blobHitScale : 1f);
    }

    // Their slosh, visual-only here, the swing played from where the ink leaves. The blob is the
    // biggest splat (the stream's front).
    public override void Replay(ProjectileSpawnData d)
    {
        if (d.velocities == null || d.splashSizes == null) return;
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

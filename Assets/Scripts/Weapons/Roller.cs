using System.Collections.Generic;
using UnityEngine;

// Roller: two flicks and a roll. Pressing attack swings it over the top, aimed up or down with the
// view: on the ground a short, wide horizontal flick (a fan of droplets) that comes down into the
// rolling position; in the air, with the frame turned on its side (vertical mode), a long, narrow
// vertical one (a line of droplets). However many droplets of a flick hit an enemy, they take its
// damage once. flickMode can keep it to one of them wherever it swings: Horizontal Only never folds
// (the Light Roller), Vertical Only always swings on its side (the Era Roller). A flick is aiming
// (the body faces the view); rolling isn't (the body, and the roller
// with it, face the way we move). Holding attack afterwards puts
// the roller down in front: moving inks a strip as wide as the roller under it (costing ink by the
// metre), starts at rollStartSpeed of our move speed and builds to rollSpeed (again after any stop), runs over enemies
// for rollDamage each time it meets one. Leaving the ground (a jump) lifts it; landing with attack
// still held puts it back down. Out of ink, it stays up. Remote copies show the pose (synced as
// PlayerController.WeaponDown) and their flicks (replayed shots); paint and damage are the owner's.
public class Roller : Weapon
{
    public enum FlickMode { Both, HorizontalOnly, VerticalOnly }

    [Header("Flick")]
    [SerializeField, Tooltip("Which flicks it has: the wide horizontal one on the ground and the narrow vertical one in the air (Both), or just one of them wherever it swings. Horizontal Only never folds: its frame doesn't turn")] FlickMode flickMode = FlickMode.Both;
    [SerializeField, Tooltip("Seconds between flicks")] float flickRate = 0.6f;
    [SerializeField, Tooltip("Seconds it swings back before the droplets leave")] float windup = 0.2f;
    [SerializeField, Tooltip("Seconds it swings on after they leave")] float followThrough = 0.15f;
    [SerializeField, Tooltip("Ink per flick, in thousandths of a full tank")] float flickInkCost = 90f;
    [SerializeField, Tooltip("Speed and gravity over each droplet's flight")] Ballistics ballistics = new Ballistics();
    [SerializeField, Tooltip("Droplets fly their whole path this many times as fast (the speeds below are before it): a snappy flick that still lands where it would")] float flickPace = 2f;
    [SerializeField, Tooltip("How much looking up or down tilts a flick (1: as much as the view)")] float aimInfluence = 1f;
    [SerializeField, Tooltip("The furthest down and up a flick can be aimed (degrees)")] Vector2 flickPitchLimits = new Vector2(-40f, 60f);

    [Header("Ground flick: wide and short")]
    [SerializeField, Tooltip("Droplets per ground flick. However many hit an enemy, they take its damage once")] int groundDroplets = 9;
    [SerializeField, Tooltip("How wide the fan is (degrees, edge to edge)")] float groundFan = 70f;
    [SerializeField, Tooltip("How far above the view it's thrown (degrees)")] float groundPitch = 5f;
    [SerializeField, Tooltip("Each droplet's launch speed, random between these (min, max), before Flick Pace")] Vector2 groundSpeed = new Vector2(4.5f, 7f);
    [SerializeField, Tooltip("Its damage, once per enemy per flick, weaker the further the droplet has flown")] DamageFalloff groundDamage = new DamageFalloff(125f, DamageFalloff.HoldThenFall(0f, 0.5f, 0.28f));
    [SerializeField, Tooltip("Each droplet's splat size (also how big it's drawn)")] int groundSplat = 14;
    [SerializeField, Tooltip("How the ground flick's droplets are drawn")] ShotLook groundLook = new ShotLook { size = 0.5f, companions = 4 };

    [Header("Air flick: narrow and long")]
    [SerializeField, Tooltip("Droplets per air flick. However many hit an enemy, they take its damage once")] int airDroplets = 7;
    [SerializeField, Tooltip("How wide the line is (degrees, edge to edge)")] float airFan = 6f;
    [SerializeField, Tooltip("How far above the view it's thrown (degrees)")] float airPitch = 20f;
    [SerializeField, Tooltip("Launch speeds from the nearest droplet to the furthest (before Flick Pace), so they land along a line")] Vector2 airSpeed = new Vector2(9f, 17f);
    [SerializeField, Tooltip("Its damage, once per enemy per flick, weaker the further the droplet has flown")] DamageFalloff airDamage = new DamageFalloff(80f, DamageFalloff.HoldThenFall(0f, 0.5f, 0.375f));
    [SerializeField, Tooltip("Each droplet's splat size (also how big it's drawn)")] int airSplat = 12;
    [SerializeField, Tooltip("How the air flick's droplets are drawn")] ShotLook airLook = new ShotLook { companions = 4 };

    [Header("Rolling")]
    [SerializeField, Tooltip("To each enemy it runs into (again only after they're clear of it)")] float rollDamage = 100f;
    [SerializeField, Tooltip("Rolling speed as it goes down, x our move speed")] float rollStartSpeed = 0.8f;
    [SerializeField, Tooltip("Rolling speed it builds up to, x our move speed")] float rollSpeed = 1.2f;
    [SerializeField, Tooltip("Seconds of rolling on the move to build up to full speed (stopping starts it over)")] float rollRampTime = 0.5f;
    [SerializeField, Tooltip("Ink per metre rolled, in thousandths of a full tank")] float rollInkPerMetre = 8f;
    [SerializeField, Tooltip("Paints a row of splats across the roller this often (metres)")] float rollPaintSpacing = 0.5f;
    [SerializeField, Tooltip("Splats painted side by side across the drum each time")] int rollSplatPoints = 3;
    [SerializeField, Tooltip("Width of the strip it inks (metres; 0: the drum's width, wider: ink thrown out past its ends)")] float rollInkWidth = 0f;
    [SerializeField, Tooltip("Each of those splats' size")] int rollSplatSize = 10;
    [SerializeField, Tooltip("Metres per second: slower than this it neither paints nor hurts")] float minRollSpeed = 1f;

    [Header("Model")]
    [SerializeField, Tooltip("The drum: spins as it rolls; its width is the strip's")] Transform roller;
    [SerializeField] float rollerRadius = 0.225f;
    [SerializeField] float rollerWidth = 2.6f;
    [SerializeField, Tooltip("From the hand to our centre line while it's down")] Vector3 rollShift = new Vector3(-0.53f, 0f, 0f);
    [SerializeField, Tooltip("Degrees down, held out in front (held in the air, about to roll)")] float carryPitch = 15f;
    [SerializeField, Tooltip("Pitch when not attacking or rolling: folded (vertical mode) and held up at our side (degrees)")] float idlePitch = -70f;
    [SerializeField, Tooltip("Where it's held at our side when idle, from the hand")] Vector3 idleOffset = new Vector3(0.1f, 0f, -0.35f);
    [SerializeField, Tooltip("The top of a flick's swing")] float raisedPitch = -80f;
    [SerializeField, Tooltip("The frame's turn (on turnAxis) for the air flick")] float verticalTurn = 90f;
    [SerializeField, Tooltip("The drum's axle, in the drum's own space (models differ)")] Vector3 spinAxis = Vector3.up;
    [SerializeField, Tooltip("The frame's turn axis, in its own space: the world's up when held level")] Vector3 turnAxis = Vector3.up;
    [SerializeField, Tooltip("How fast it eases between poses")] float poseSpeed = 18f;
    [SerializeField, Tooltip("Flip if the drum turns the wrong way")] float spinDirection = 1f;

    enum State { Idle, Flick, Held }

    ControlLayer input;
    State state;
    bool flickVertical, wasPressed, dry, local;
    float flickStart = -10f, released = -20f, rolling, sincePaint; // rolling: seconds on the move with it down
    CharacterController body;
    Vector3 lastPos, homePos;
    Quaternion rollerHome, frameHome;
    Transform frame;
    float turn;  // the frame's current turn (degrees)
    bool wantFlick; // pressed, waiting until a flick can go (just out of swim form, say)
    float reach = 1.7f, drop, spin;
    float pitch, shift, fold; // current pose: pitch down (degrees), shift towards our centre line (0-1), idle offset (0-1)
    float posedFlick = -1f, pitchFrom, turnFrom, foldFrom; // the pose a flick swings from
    Ballistics paced;
    HashSet<Object> flickHits;
    readonly HashSet<Object> underRoller = new HashSet<Object>();
    static readonly Collider[] overlaps = new Collider[16];

    public bool ScriptedFire { get; set; } // tests: hold the trigger
    public bool Down { get; private set; }  // rolling: on the floor
    public int Flicks { get; private set; }
    public float RollInkSpent { get; private set; } // tests
    public bool LastFlickInAir { get; private set; } // the last flick was the vertical one
    public FlickMode Mode { get => flickMode; set => flickMode = value; }
    bool Folds => flickMode != FlickMode.HorizontalOnly;
    public Vector3[] LastFlick { get; private set; } // launch velocities of the last flick (tests)
    public DamageFalloff GroundDamage => groundDamage;
    public DamageFalloff AirDamage => airDamage;
    public float RollDamage => rollDamage;
    public float RollSpeed => rollSpeed;
    public float RollStartSpeed => rollStartSpeed;
    public float RollRampTime => rollRampTime;
    public float RollInkPerMetre => rollInkPerMetre;
    public float FlickInkCost => flickInkCost;
    public float RollerWidth => rollerWidth;
    public float RollInkWidth => rollInkWidth > 0f ? rollInkWidth : rollerWidth;
    public float FlickRate => flickRate;
    public Transform Drum => roller;
    public float FrameTurn => turn;          // tests: 0 unfolded, verticalTurn folded (vertical mode)
    public float Pitch => pitch;             // tests: the pose's tilt (degrees down)
    public float IdlePitch => idlePitch;
    public float FlickPace => flickPace;
    public ShotLook GroundLook => groundLook;
    public float VerticalTurn => verticalTurn;
    public bool Flicking => state == State.Flick;
    public float Windup => windup;
    public float FollowThrough => followThrough;
    public string Source => DisplayName;

    public override void Equip(PlayerController owner)
    {
        base.Equip(owner);
        local = owner.IsLocalPlayer;
        enabled = true; // remote copies still pose and spin
        WeaponCameraAim aim = GetComponent<WeaponCameraAim>();
        if (aim != null) aim.enabled = false; // posed by the roller itself
    }

    void Start()
    {
        input = new ControlLayer();
        input.Enable();
        homePos = transform.localPosition;
        if (roller != null)
        {
            rollerHome = roller.localRotation;
            frame = roller.parent != transform ? roller.parent : null; // RollerFrame
            if (!Folds) frame = null; // (the drum's parent may be the whole body)
            if (frame != null) frameHome = frame.localRotation;
            Vector3 at = transform.InverseTransformPoint(roller.position);
            reach = at.z;
            drop = at.y;
        }
        pitch = idlePitch;
        turn = Folds ? verticalTurn : 0f;
        fold = 1f;
        paced = ballistics.Faster(flickPace);
        if (Owner != null) { lastPos = Owner.transform.position; body = Owner.GetComponent<CharacterController>(); }
    }

    protected override void OnDestroy()
    {
        base.OnDestroy();
        input?.Disable();
        input?.Dispose();
        if (Owner != null && local) { Owner.WeaponSpeedMultiplier = 1f; Owner.WeaponDown = false; }
    }

    void Update()
    {
        PlayerController player = Owner;
        if (player == null) return;
        Vector3 moved = player.transform.position - lastPos;
        lastPos = player.transform.position;
        moved.y = 0f;
        float dt = Mathf.Max(Time.deltaTime, 1e-5f);

        if (local) Think(player, moved, dt);
        else
        {
            Down = player.WeaponDown;
            if (state == State.Flick && Time.time - flickStart >= windup + followThrough) state = State.Idle; // their swing played out
        }

        // The drum turns with the ground going by under it.
        if (roller != null && Down)
        {
            spin += Vector3.Dot(moved, BodyForward(player)) / rollerRadius * Mathf.Rad2Deg * spinDirection;
            roller.localRotation = rollerHome * Quaternion.AngleAxis(spin, spinAxis);
        }
    }

    void Think(PlayerController player, Vector3 moved, float dt)
    {
        bool hardware = !player.IsTestPlayer && player.ScriptedInput == null && !InputGate.Blocked && input.Weapon.Attack.ReadValue<float>() != 0;
        bool held = !InputGate.GameLocked && !player.ActionLocked && !player.IsDead && (ScriptedFire || hardware);
        player.AttackHeld = held;
        bool pressed = held && !player.IsSquid;
        if (pressed && !wasPressed) wantFlick = true; // a press (or coming out of swim form with it held)
        if (!pressed) wantFlick = false;
        wasPressed = pressed;

        if (!pressed && state == State.Held) state = State.Idle;
        if (player.IsSquid || player.IsDead || InputGate.GameLocked) state = State.Idle;

        // Goes as soon as it can: retried each frame while held (the kid form may still be coming up).
        if (wantFlick && state != State.Flick && Time.time - flickStart >= flickRate && player.ConsumeInk(flickInkCost * 0.001f))
        {
            wantFlick = false;
            player.MarkAiming(); // face the view for the swing
            state = State.Flick;
            flickStart = Time.time;
            flickVertical = flickMode == FlickMode.VerticalOnly || (flickMode == FlickMode.Both && !OnGround(player));
            flickHits = new HashSet<Object>();
            Flicks++;
        }
        if (state == State.Flick)
        {
            player.MarkAiming();
            float t = Time.time - flickStart;
            if (t >= windup && released != flickStart) Release(player);
            if (t >= windup + followThrough) { state = pressed ? State.Held : State.Idle; dry = false; }
        }

        // Down while held on the ground with ink to roll on.
        bool wantDown = state == State.Held && OnGround(player) && !dry;
        if (wantDown)
        {
            float metres = moved.magnitude;
            if (metres > 0f)
            {
                if (player.ConsumeInk(metres * rollInkPerMetre * 0.001f)) RollInkSpent += metres * rollInkPerMetre * 0.001f;
                else { dry = true; wantDown = false; }
            }
        }
        if (wantDown && !Down) { sincePaint = rollPaintSpacing; underRoller.Clear(); }
        Down = wantDown;
        player.WeaponDown = Down;
        bool onTheMove = Down && moved.magnitude / dt >= minRollSpeed;
        rolling = onTheMove ? rolling + dt : 0f;
        player.WeaponSpeedMultiplier = Down ? Mathf.Lerp(rollStartSpeed, rollSpeed, rolling / rollRampTime) : 1f;

        if (onTheMove)
        {
            sincePaint += moved.magnitude;
            if (sincePaint >= rollPaintSpacing) { sincePaint = 0f; Paint(player); }
            RunOver(player);
        }
        else underRoller.Clear();
    }

    // The droplets leave from in front of us, at the hand's height.
    void Release(PlayerController player)
    {
        released = flickStart;
        Vector3 flat = Flat(player); // the view's heading (the body faces it while flicking)
        Vector3 origin = Pivot() + flat * (reach * 0.8f);
        float aimUp = -player.RaisedAimPitch * aimInfluence; // looking up raises it, down lowers it
        Vector3 inherit = body != null ? body.velocity : Vector3.zero;
        inherit.y = 0f; // running carries into the droplets; jumping doesn't
        float facing = player.transform.eulerAngles.y;
        int n = flickVertical ? airDroplets : groundDroplets;
        var launches = new Vector3[n];
        var velocities = new float[n * 3];
        var sizes = new int[n];
        var visible = new bool[n];
        for (int i = 0; i < n; i++)
        {
            float k = n > 1 ? i / (n - 1f) : 0.5f;
            float yawOff, up, speed;
            if (flickVertical)
            {
                yawOff = Random.Range(-airFan, airFan) * 0.5f;
                up = airPitch + Random.Range(-3f, 3f);
                speed = Mathf.Lerp(airSpeed.x, airSpeed.y, k);
            }
            else
            {
                yawOff = Mathf.Lerp(-groundFan, groundFan, k) * 0.5f + Random.Range(-3f, 3f);
                up = groundPitch + Random.Range(-3f, 3f);
                speed = Random.Range(groundSpeed.x, groundSpeed.y);
            }
            up = Mathf.Clamp(up + aimUp, flickPitchLimits.x, flickPitchLimits.y);
            Vector3 launch = Quaternion.Euler(-up, facing + yawOff, 0f) * Vector3.forward * (speed * flickPace);
            launches[i] = launch;
            velocities[i * 3] = launch.x; velocities[i * 3 + 1] = launch.y; velocities[i * 3 + 2] = launch.z;
            sizes[i] = flickVertical ? airSplat : groundSplat;
            visible[i] = true;
            Fire(origin, launch, inherit, sizes[i], player.Team, player.OwnerId, true, flickVertical ? airDamage : groundDamage, flickHits, flickVertical);
        }
        LastFlick = launches;
        LastFlickInAir = flickVertical;
        NetGameManager.Instance?.BroadcastShots(player, origin, inherit, velocities, sizes, visible);
    }

    void Fire(Vector3 from, Vector3 launch, Vector3 inherit, int size, int team, ulong ownerId, bool authoritative, DamageFalloff damage, HashSet<Object> hits, bool inAir)
    {
        ProjectileManager.Fire(from, launch, size, team, true, authoritative, ownerId, 0f,
                               ballistics: paced ?? ballistics, falloff: damage, inherit: inherit, source: Source, hitGroup: hits,
                               look: inAir ? airLook : groundLook);
    }

    // Their flick, visual-only here, with the swing played from the top (the droplets leave at the
    // top of it). A narrow spread is an air flick: the frame turns on its side for it.
    public override void Replay(ProjectileSpawnData d)
    {
        if (d.velocities == null || d.splashSizes == null) return;
        Vector3 inherit = d.inherit != null ? (Vector3)d.inherit : Vector3.zero;
        int n = Mathf.Min(d.splashSizes.Length, d.velocities.Length / 3);
        if (!local && n > 0)
        {
            float first = Mathf.Atan2(d.velocities[0], d.velocities[2]) * Mathf.Rad2Deg, minYaw = 0f, maxYaw = 0f;
            for (int i = 1; i < n; i++)
            {
                float yaw = Mathf.DeltaAngle(first, Mathf.Atan2(d.velocities[i * 3], d.velocities[i * 3 + 2]) * Mathf.Rad2Deg);
                minYaw = Mathf.Min(minYaw, yaw);
                maxYaw = Mathf.Max(maxYaw, yaw);
            }
            flickVertical = maxYaw - minYaw < 20f;
            flickStart = Time.time - windup;
            state = State.Flick;
            Flicks++;
        }
        for (int i = 0; i < n; i++)
            Fire(d.origin, new Vector3(d.velocities[i * 3], d.velocities[i * 3 + 1], d.velocities[i * 3 + 2]), inherit, d.splashSizes[i], d.team, d.shooterSteamId, false, null, null, flickVertical);
    }

    // A row of splats across the drum, onto whatever's under it.
    void Paint(PlayerController player)
    {
        Vector3 centre = roller != null ? roller.position : transform.position;
        Vector3 across = Vector3.Cross(Vector3.up, BodyForward(player));
        float half = RollInkWidth * 0.5f;
        for (int i = 0; i < rollSplatPoints; i++)
        {
            float k = rollSplatPoints > 1 ? i / (rollSplatPoints - 1f) : 0.5f;
            Vector3 at = centre + across * Mathf.Lerp(-half * 0.75f, half * 0.75f, k);
            if (!Physics.Raycast(at + Vector3.up, Vector3.down, out RaycastHit hit, 1f + rollerRadius * 4f, PhysicsLayers.Environment, QueryTriggerInteraction.Ignore)) continue;
            SurfaceInkManager ink = hit.collider.GetComponent<SurfaceInkManager>();
            if (ink != null) ink.SplatAt(hit, rollSplatSize, player.Team);
        }
    }

    // Enemies (and their subs) the drum meets: hit once as they come under it.
    void RunOver(PlayerController player)
    {
        Vector3 centre = roller != null ? roller.position : transform.position;
        var extents = new Vector3(rollerWidth * 0.5f, rollerRadius + 0.4f, rollerRadius + 0.3f);
        int n = Physics.OverlapBoxNonAlloc(centre, extents, overlaps, Quaternion.LookRotation(BodyForward(player)), LayerMask.GetMask("Hitbox"), QueryTriggerInteraction.Collide);
        var now = new HashSet<Object>();
        for (int i = 0; i < n; i++)
        {
            PlayerHitbox hitbox = overlaps[i].GetComponentInParent<PlayerHitbox>();
            if (hitbox != null)
            {
                if (hitbox.Team == player.Team) continue;
                now.Add(hitbox);
                if (!underRoller.Contains(hitbox)) hitbox.TakeDamage(rollDamage, player.Team, player.OwnerId, Source);
                continue;
            }
            SubDevice sub = overlaps[i].GetComponentInParent<SubDevice>();
            if (sub != null && sub.Team != player.Team)
            {
                now.Add(sub);
                if (!underRoller.Contains(sub)) sub.TakeDamage(rollDamage, player.Team, player.OwnerId);
            }
        }
        underRoller.Clear();
        underRoller.UnionWith(now);
    }

    void LateUpdate()
    {
        PlayerController player = Owner;
        if (player == null) return;
        // Idle (not attacking or rolling; for remotes, not down): folded and held up at our side.
        // Held in the air (or out of ink): out in front, ready to roll.
        bool idle = !Down && !(local && state == State.Held);
        float targetPitch = idle ? idlePitch : carryPitch, targetShift = 0f, targetTurn = idle && Folds ? verticalTurn : 0f, targetFold = idle ? 1f : 0f;
        bool snap = false;
        if (state == State.Flick) // ours, or theirs replayed
        {
            // Up and over the top from wherever it was: the ground flick, unfolded, comes down into
            // the rolling position (centred); the air flick, folded on its side, down to the carry.
            if (posedFlick != flickStart) { posedFlick = flickStart; pitchFrom = pitch; turnFrom = turn; foldFrom = fold; }
            float t = Time.time - flickStart;
            float back = Mathf.SmoothStep(0f, 1f, t / windup), through = Mathf.SmoothStep(0f, 1f, (t - windup) / followThrough);
            float end = flickVertical ? carryPitch : GroundPitch(player);
            targetPitch = t < windup ? Mathf.Lerp(pitchFrom, raisedPitch, back) : Mathf.Lerp(raisedPitch, end, through);
            if (!flickVertical) shift = targetShift = t < windup ? 0f : through;
            turn = Mathf.Lerp(turnFrom, flickVertical ? verticalTurn : 0f, Mathf.Clamp01(t / (windup * 0.5f))); // set before the top
            fold = Mathf.Lerp(foldFrom, 0f, back);
            snap = true;
        }
        else if (Down)
        {
            targetPitch = GroundPitch(player);
            targetShift = 1f;
        }
        float ease = 1f - Mathf.Exp(-poseSpeed * Time.deltaTime);
        pitch = snap ? targetPitch : Mathf.Lerp(pitch, targetPitch, ease);
        shift = Mathf.Lerp(shift, targetShift, ease);
        if (!snap) { turn = Mathf.Lerp(turn, targetTurn, ease); fold = Mathf.Lerp(fold, targetFold, ease); }
        if (frame != null) frame.localRotation = frameHome * Quaternion.AngleAxis(turn, turnAxis);
        transform.localPosition = homePos + rollShift * shift + idleOffset * fold;
        transform.rotation = Quaternion.Euler(pitch, BodyYaw(player), 0f); // along the body (which faces the view while flicking)
    }

    // Tipped down until the drum sits on the floor in front.
    float GroundPitch(PlayerController player)
    {
        Vector3 pivot = Pivot();
        Vector3 ahead = pivot + BodyForward(player) * reach * 0.9f;
        float floor = Physics.Raycast(ahead + Vector3.up, Vector3.down, out RaycastHit hit, 4f, PhysicsLayers.Environment, QueryTriggerInteraction.Ignore)
            ? hit.point.y : player.transform.position.y;
        float sin = (pivot.y + drop - floor - rollerRadius) / Mathf.Max(reach, 0.1f);
        return Mathf.Asin(Mathf.Clamp(sin, -0.5f, 0.95f)) * Mathf.Rad2Deg;
    }

    Vector3 Pivot() => transform.parent != null ? transform.parent.TransformPoint(homePos + rollShift * shift + idleOffset * fold) : transform.position;

    // isGrounded alone drops out on frames that barely move us down (high frame rates, slow motion):
    // also standing on something just under the feet and not rising (a jump is).
    bool OnGround(PlayerController player)
    {
        if (player.IsGrounded) return true;
        if (body != null && body.velocity.y > 0.5f) return false;
        return Physics.Raycast(player.transform.position + Vector3.up * 0.1f, Vector3.down, 0.25f, PhysicsLayers.Environment, QueryTriggerInteraction.Ignore);
    }

    // The kid model's heading: the roller is held along it.
    float BodyYaw(PlayerController player) => transform.parent != null ? transform.parent.eulerAngles.y : player.transform.eulerAngles.y;
    Vector3 BodyForward(PlayerController player) => Quaternion.Euler(0f, BodyYaw(player), 0f) * Vector3.forward;

    // The view's heading.
    static Vector3 Flat(PlayerController player)
    {
        Vector3 f = player.transform.forward;
        f.y = 0f;
        return f.sqrMagnitude > 1e-6f ? f.normalized : Vector3.forward;
    }
}

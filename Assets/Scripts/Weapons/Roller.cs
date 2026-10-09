using System.Collections.Generic;
using UnityEngine;

// Roller: two flicks and a roll. Pressing attack swings it over the top, aimed up or down with the
// view: on the ground a short, wide horizontal flick (a fan of droplets) that comes down into the
// rolling position; in the air, with the frame turned on its side (vertical mode), a long, narrow
// vertical one (a line of droplets). However many droplets of a flick hit an enemy, they take its
// damage once. A flick is aiming (the body faces the view); rolling isn't (the body, and the roller
// with it, face the way we move). Holding attack afterwards puts
// the roller down in front: moving inks a strip as wide as the roller under it (costing ink by the
// metre), starts at rollStartSpeed of our move speed and builds to rollSpeed (again after any stop), runs over enemies
// for rollDamage each time it meets one. Leaving the ground (a jump) lifts it; landing with attack
// still held puts it back down. Out of ink, it stays up. Remote copies show the pose (synced as
// PlayerController.WeaponDown) and their flicks (replayed shots); paint and damage are the owner's.
public class Roller : Weapon
{
    [Header("Flick")]
    [SerializeField] float flickRate = 0.6f;          // seconds between flicks
    [SerializeField] float windup = 0.2f;             // swing back before the droplets leave
    [SerializeField] float followThrough = 0.15f;     // swing on after they do
    [SerializeField] float flickInkCost = 90f;        // x0.001 of a full tank
    [SerializeField] GameObject projectile;
    [SerializeField] Ballistics ballistics = new Ballistics();
    [SerializeField] float flickPace = 2f;            // droplets fly their path this many times as fast (speeds below are before it)
    [SerializeField] float dropletScale = 2f;         // how big the droplets look, x a shot's size for their splat
    [SerializeField] float aimInfluence = 1f;         // how much looking up/down tilts a flick (1: as much as the view)
    [SerializeField] Vector2 flickPitchLimits = new Vector2(-40f, 60f); // degrees (down, up) a flick can be aimed

    [Header("Ground flick: wide and short")]
    [SerializeField] int groundDroplets = 9;
    [SerializeField] float groundFan = 70f;           // degrees across
    [SerializeField] float groundPitch = 5f;          // degrees up
    [SerializeField] Vector2 groundSpeed = new Vector2(4.5f, 7f);
    [SerializeField] DamageFalloff groundDamage = new DamageFalloff(125f, 35f, 0.5f); // once per enemy per flick, weaker the further it's flown
    [SerializeField] int groundSplat = 14;

    [Header("Air flick: narrow and long")]
    [SerializeField] int airDroplets = 7;
    [SerializeField] float airFan = 6f;
    [SerializeField] float airPitch = 20f;
    [SerializeField] Vector2 airSpeed = new Vector2(9f, 17f); // spread nearest to furthest, so they land along a line
    [SerializeField] DamageFalloff airDamage = new DamageFalloff(80f, 30f, 0.5f);
    [SerializeField] int airSplat = 12;

    [Header("Rolling")]
    [SerializeField] float rollDamage = 100f;         // to each enemy it runs into (again only after they're clear of it)
    [SerializeField] float rollStartSpeed = 0.8f;     // x our move speed as it goes down...
    [SerializeField] float rollSpeed = 1.2f;          // ...building to this
    [SerializeField] float rollRampTime = 0.5f;       // of rolling on the move (stopping starts it over)
    [SerializeField] float rollInkPerMetre = 8f;      // x0.001 of a full tank
    [SerializeField] float rollPaintSpacing = 0.5f;   // paints a row of splats across the roller this often (metres)
    [SerializeField] int rollSplatPoints = 3;         // splats across its width
    [SerializeField] int rollSplatSize = 10;
    [SerializeField] float minRollSpeed = 1f;         // metres per second: slower than this it neither paints nor hurts

    [Header("Model")]
    [SerializeField] Transform roller;                // the drum: spins as it rolls; its width is the strip's
    [SerializeField] float rollerRadius = 0.225f;
    [SerializeField] float rollerWidth = 2.6f;
    [SerializeField] Vector3 rollShift = new Vector3(-0.53f, 0f, 0f); // from the hand to our centre line while it's down
    [SerializeField] float carryPitch = 15f;          // degrees down, held out in front (held in the air, about to roll)
    [SerializeField] float idlePitch = -70f;          // not attacking or rolling: folded (vertical mode) and held up...
    [SerializeField] Vector3 idleOffset = new Vector3(0.1f, 0f, -0.35f); // ...at our side
    [SerializeField] float raisedPitch = -80f;        // the top of a flick's swing
    [SerializeField] float verticalTurn = 90f;        // the frame's turn (on its local y) for the air flick
    [SerializeField] float poseSpeed = 18f;           // how fast it eases between poses
    [SerializeField] float spinDirection = 1f;        // flip if the drum turns the wrong way

    enum State { Idle, Flick, Held }

    ControlLayer input;
    State state;
    bool flickInAir, wasPressed, dry, local;
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
    MaterialPropertyBlock block;

    public bool ScriptedFire { get; set; } // tests: hold the trigger
    public bool Down { get; private set; }  // rolling: on the floor
    public int Flicks { get; private set; }
    public float RollInkSpent { get; private set; } // tests
    public bool LastFlickInAir { get; private set; }
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
    public float FlickRate => flickRate;
    public Transform Drum => roller;
    public float FrameTurn => turn;          // tests: 0 unfolded, verticalTurn folded (vertical mode)
    public float Pitch => pitch;             // tests: the pose's tilt (degrees down)
    public float IdlePitch => idlePitch;
    public float FlickPace => flickPace;
    public float DropletScale => dropletScale;
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
            if (frame != null) frameHome = frame.localRotation;
            Vector3 at = transform.InverseTransformPoint(roller.position);
            reach = at.z;
            drop = at.y;
        }
        pitch = idlePitch;
        turn = verticalTurn;
        fold = 1f;
        paced = ballistics.Faster(flickPace);
        if (Owner != null) { lastPos = Owner.transform.position; body = Owner.GetComponent<CharacterController>(); }
        Tint();
        NetGameManager.TeamColoursChanged += Tint;
    }

    void OnDestroy()
    {
        input?.Disable();
        input?.Dispose();
        NetGameManager.TeamColoursChanged -= Tint;
        if (Owner != null && local) { Owner.WeaponSpeedMultiplier = 1f; Owner.WeaponDown = false; }
    }

    // The ink-coloured parts (materials named Ink...) in the team colour.
    void Tint()
    {
        if (Owner == null) return;
        block ??= new MaterialPropertyBlock();
        foreach (Renderer r in GetComponentsInChildren<Renderer>(true))
        {
            Material[] mats = r.sharedMaterials;
            for (int i = 0; i < mats.Length; i++)
            {
                if (mats[i] == null || !mats[i].name.StartsWith("Ink")) continue;
                r.GetPropertyBlock(block, i);
                block.SetColor("_Color", Owner.TeamColour);
                r.SetPropertyBlock(block, i);
            }
        }
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
            roller.localRotation = rollerHome * Quaternion.AngleAxis(spin, Vector3.up);
        }
    }

    void Think(PlayerController player, Vector3 moved, float dt)
    {
        bool hardware = !player.IsTestPlayer && player.ScriptedInput == null && !InputGate.Blocked && input.Weapon.Attack.ReadValue<float>() != 0;
        bool held = !InputGate.MatchLocked && !player.IsDead && (ScriptedFire || hardware);
        player.AttackHeld = held;
        bool pressed = held && !player.IsSquid;
        if (pressed && !wasPressed) wantFlick = true; // a press (or coming out of swim form with it held)
        if (!pressed) wantFlick = false;
        wasPressed = pressed;

        if (!pressed && state == State.Held) state = State.Idle;
        if (player.IsSquid || player.IsDead || InputGate.MatchLocked) state = State.Idle;

        // Goes as soon as it can: retried each frame while held (the kid form may still be coming up).
        if (wantFlick && state != State.Flick && Time.time - flickStart >= flickRate && player.ConsumeInk(flickInkCost * 0.001f))
        {
            wantFlick = false;
            player.MarkAiming(); // face the view for the swing
            state = State.Flick;
            flickStart = Time.time;
            flickInAir = !OnGround(player);
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
        float aimUp = -player.CameraPitch * aimInfluence; // looking up raises it, down lowers it
        Vector3 inherit = body != null ? body.velocity : Vector3.zero;
        inherit.y = 0f; // running carries into the droplets; jumping doesn't
        float facing = player.transform.eulerAngles.y;
        int n = flickInAir ? airDroplets : groundDroplets;
        var launches = new Vector3[n];
        var velocities = new float[n * 3];
        var sizes = new int[n];
        var visible = new bool[n];
        for (int i = 0; i < n; i++)
        {
            float k = n > 1 ? i / (n - 1f) : 0.5f;
            float yawOff, up, speed;
            if (flickInAir)
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
            sizes[i] = flickInAir ? airSplat : groundSplat;
            visible[i] = true;
            Fire(origin, launch, inherit, sizes[i], player.Team, player.OwnerId, true, flickInAir ? airDamage : groundDamage, flickHits);
        }
        LastFlick = launches;
        LastFlickInAir = flickInAir;
        NetGameManager.Instance?.BroadcastShots(player, origin, inherit, velocities, sizes, visible);
    }

    void Fire(Vector3 from, Vector3 launch, Vector3 inherit, int size, int team, ulong ownerId, bool authoritative, DamageFalloff damage, HashSet<Object> hits)
    {
        ProjectileManager.Fire(projectile, from, launch, size, team, true, authoritative, ownerId, damage?.maxDamage ?? 0f,
                               ballistics: paced ?? ballistics, falloff: damage, inherit: inherit, source: Source, hitGroup: hits, visualScale: dropletScale);
    }

    // Their flick, visual-only here, with the swing played from the top (the droplets leave at the
    // top of it). A narrow spread is an air flick: the frame turns on its side for it.
    public override void Replay(ProjectileSpawnData d)
    {
        if (projectile == null || d.velocities == null || d.splashSizes == null) return;
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
            flickInAir = maxYaw - minYaw < 20f;
            flickStart = Time.time - windup;
            state = State.Flick;
            Flicks++;
        }
        for (int i = 0; i < n; i++)
            Fire(d.origin, new Vector3(d.velocities[i * 3], d.velocities[i * 3 + 1], d.velocities[i * 3 + 2]), inherit, d.splashSizes[i], d.team, d.shooterSteamId, false, null, null);
    }

    // A row of splats across the drum, onto whatever's under it.
    void Paint(PlayerController player)
    {
        Vector3 centre = roller != null ? roller.position : transform.position;
        Vector3 across = Vector3.Cross(Vector3.up, BodyForward(player));
        float half = rollerWidth * 0.5f;
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
        float targetPitch = idle ? idlePitch : carryPitch, targetShift = 0f, targetTurn = idle ? verticalTurn : 0f, targetFold = idle ? 1f : 0f;
        bool snap = false;
        if (state == State.Flick) // ours, or theirs replayed
        {
            // Up and over the top from wherever it was: the ground flick, unfolded, comes down into
            // the rolling position (centred); the air flick, folded on its side, down to the carry.
            if (posedFlick != flickStart) { posedFlick = flickStart; pitchFrom = pitch; turnFrom = turn; foldFrom = fold; }
            float t = Time.time - flickStart;
            float back = Mathf.SmoothStep(0f, 1f, t / windup), through = Mathf.SmoothStep(0f, 1f, (t - windup) / followThrough);
            float end = flickInAir ? carryPitch : GroundPitch(player);
            targetPitch = t < windup ? Mathf.Lerp(pitchFrom, raisedPitch, back) : Mathf.Lerp(raisedPitch, end, through);
            if (!flickInAir) shift = targetShift = t < windup ? 0f : through;
            turn = Mathf.Lerp(turnFrom, flickInAir ? verticalTurn : 0f, Mathf.Clamp01(t / (windup * 0.5f))); // set before the top
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
        if (frame != null) frame.localRotation = frameHome * Quaternion.AngleAxis(turn, Vector3.up);
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

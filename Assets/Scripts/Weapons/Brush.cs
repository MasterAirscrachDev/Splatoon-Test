using System.Collections.Generic;
using UnityEngine;

// Inkbrush: quick side-to-side swipes and a fast, narrow brush along the floor. Built from the Roller:
// pressing attack swipes the brush across in front of us, the other way from the last swipe, flinging
// a short fan of droplets that leans the way the head's going. Holding attack afterwards drags the
// bristles along the floor: moving inks a strip as wide as the head under it (costing ink by the
// metre), much faster than a roller (starting at brushStartSpeed of our move speed and building to
// brushSpeed, again after any stop), and catches enemies for brushDamage each time it meets one.
// Leaving the ground lifts it; out of ink, it stays up. Remote copies show the pose (synced as
// PlayerController.WeaponDown) and their swipes (replayed shots); paint and damage are the owner's.
public class Brush : Weapon
{
    [Header("Swipe")]
    [SerializeField] float swipeRate = 0.2f;          // seconds between swipes
    [SerializeField] float swipeTime = 0.16f;         // the swing across
    [SerializeField] float releaseAt = 0.5f;          // share of the swing when the droplets leave
    [SerializeField] float swipeArc = 80f;            // degrees, from one side to the other
    [SerializeField] float swipeInkCost = 20f;        // x0.001 of a full tank
    [SerializeField] Ballistics ballistics = new Ballistics();
    [SerializeField] float swipePace = 2f;            // droplets fly their path this many times as fast (speeds below are before it)
    [SerializeField] float aimInfluence = 1f;         // how much looking up/down tilts a swipe
    [SerializeField] Vector2 swipePitchLimits = new Vector2(-40f, 50f);
    [SerializeField] int droplets = 5;
    [SerializeField] float fan = 40f;                 // degrees across
    [SerializeField] float lean = 12f;                // the fan's lean the way the head swings (degrees)
    [SerializeField] float swipePitch = 4f;           // degrees up from the view
    [SerializeField] Vector2 swipePoseLimits = new Vector2(-50f, 50f); // the swing follows the view's pitch (down, up), like a sword
    [SerializeField] Vector2 dropletSpeed = new Vector2(7f, 9.5f);
    [SerializeField] DamageFalloff swipeDamage = new DamageFalloff(40f, DamageFalloff.HoldThenFall(0f, 0.4f, 0.5f)); // once per enemy per swipe
    [SerializeField] int swipeSplat = 9;

    [Header("Brushing")]
    [SerializeField] float brushDamage = 25f;         // to each enemy it meets (again only after they're clear of it)
    [SerializeField] float brushStartSpeed = 1.2f;    // x our move speed as it goes down...
    [SerializeField] float brushSpeed = 1.9f;         // ...building to this
    [SerializeField] float brushRampTime = 0.3f;      // of brushing on the move (stopping starts it over)
    [SerializeField] float brushInkPerMetre = 3f;     // x0.001 of a full tank
    [SerializeField] float brushPaintSpacing = 0.2f;  // paints a row of splats across the head this often (metres)
    [SerializeField] int brushSplatPoints = 3;        // splats across its width
    [SerializeField] int brushSplatSize = 3;          // three side by side: the strip comes out about headWidth
    [SerializeField] float minBrushSpeed = 1f;        // metres per second: slower than this it neither paints nor hurts

    [Header("Model")]
    [SerializeField] Transform head;                  // the bristles' middle: what meets the floor
    [SerializeField] float headWidth = 2f;            // the strip it inks (wider than the bristles: the ink spreads)
    [SerializeField] float headThickness = 0.15f;     // its centre sits this high off the floor
    [SerializeField] Vector3 brushShift = new Vector3(-0.5f, 0f, 0f); // from the hand to our centre line while it's down
    [SerializeField] float carryPitch = 10f;          // degrees down, held out in front
    [SerializeField] float idlePitch = -35f;          // not attacking or brushing: held up...
    [SerializeField] Vector3 idleOffset = new Vector3(0.05f, 0f, -0.3f); // ...at our side
    [SerializeField] float poseSpeed = 18f;

    enum State { Idle, Swipe, Held }

    ControlLayer input;
    State state;
    bool wasPressed, dry, local, wantSwipe;
    float swipeStart = -10f, released = -20f, brushing, sincePaint; // brushing: seconds on the move with it down
    int swipeSign = -1;                               // the way the current swipe goes: +1 left to right
    CharacterController body;
    Vector3 lastPos, homePos;
    float reach = 1.6f, drop;
    float pitch, yawOff, shift, fold;                 // current pose
    Ballistics paced;
    HashSet<Object> swipeHits;
    readonly HashSet<Object> underHead = new HashSet<Object>();
    static readonly Collider[] overlaps = new Collider[16];

    public bool ScriptedFire { get; set; } // tests: hold the trigger
    public bool Down { get; private set; }  // brushing: on the floor
    public int Swipes { get; private set; }
    public int LastSwipeSign { get; private set; }     // +1 swung left to right, -1 right to left
    public Vector3[] LastSwipe { get; private set; }   // launch velocities of the last swipe (tests)
    public float BrushInkSpent { get; private set; }
    public float YawOffset => yawOff;
    public DamageFalloff SwipeDamage => swipeDamage;
    public float BrushDamage => brushDamage;
    public float BrushSpeed => brushSpeed;
    public float BrushStartSpeed => brushStartSpeed;
    public float BrushRampTime => brushRampTime;
    public float HeadWidth => headWidth;
    public float SwipeRate => swipeRate;
    public float SwipeTime => swipeTime;
    public bool Swiping => state == State.Swipe;
    public Transform Head => head;
    public string Source => DisplayName;

    public override void Equip(PlayerController owner)
    {
        base.Equip(owner);
        local = owner.IsLocalPlayer;
        enabled = true; // remote copies still pose
        WeaponCameraAim aim = GetComponent<WeaponCameraAim>();
        if (aim != null) aim.enabled = false; // posed by the brush itself
    }

    void Start()
    {
        input = new ControlLayer();
        input.Enable();
        homePos = transform.localPosition;
        if (head != null)
        {
            Vector3 at = transform.InverseTransformPoint(head.position);
            reach = at.z;
            drop = at.y;
        }
        pitch = idlePitch;
        fold = 1f;
        paced = ballistics.Faster(swipePace);
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
            if (state == State.Swipe && Time.time - swipeStart >= swipeTime) state = State.Idle; // their swipe played out
        }
    }

    void Think(PlayerController player, Vector3 moved, float dt)
    {
        bool hardware = !player.IsTestPlayer && player.ScriptedInput == null && !InputGate.Blocked && input.Weapon.Attack.ReadValue<float>() != 0;
        bool held = !InputGate.MatchLocked && !player.IsDead && (ScriptedFire || hardware);
        player.AttackHeld = held;
        bool pressed = held && !player.IsSquid;
        if (pressed && !wasPressed) wantSwipe = true; // a press (or coming out of swim form with it held)
        if (!pressed) wantSwipe = false;
        wasPressed = pressed;

        if (!pressed && state == State.Held) state = State.Idle;
        if (player.IsSquid || player.IsDead || InputGate.MatchLocked) state = State.Idle;

        // Goes as soon as it can: retried each frame while held (the kid form may still be coming up).
        if (wantSwipe && state != State.Swipe && Time.time - swipeStart >= swipeRate && player.ConsumeInk(swipeInkCost * 0.001f))
        {
            wantSwipe = false;
            player.MarkAiming(); // face the view for the swing
            BeginSwipe();
            swipeHits = new HashSet<Object>();
        }
        if (state == State.Swipe)
        {
            player.MarkAiming();
            float t = Time.time - swipeStart;
            if (t >= swipeTime * releaseAt && released != swipeStart) Release(player);
            if (t >= swipeTime) { state = pressed ? State.Held : State.Idle; dry = false; }
        }

        // Down while held on the ground with ink to brush with.
        bool wantDown = state == State.Held && OnGround(player) && !dry;
        if (wantDown)
        {
            float metres = moved.magnitude;
            if (metres > 0f)
            {
                if (player.ConsumeInk(metres * brushInkPerMetre * 0.001f)) BrushInkSpent += metres * brushInkPerMetre * 0.001f;
                else { dry = true; wantDown = false; }
            }
        }
        if (wantDown && !Down) { sincePaint = brushPaintSpacing; underHead.Clear(); }
        Down = wantDown;
        player.WeaponDown = Down;
        bool onTheMove = Down && moved.magnitude / dt >= minBrushSpeed;
        brushing = onTheMove ? brushing + dt : 0f;
        player.WeaponSpeedMultiplier = Down ? Mathf.Lerp(brushStartSpeed, brushSpeed, brushing / brushRampTime) : 1f;

        if (onTheMove)
        {
            sincePaint += moved.magnitude;
            if (sincePaint >= brushPaintSpacing) { sincePaint = 0f; Paint(player); }
            RunOver(player);
        }
        else underHead.Clear();
    }

    // Each swipe goes the other way from the last.
    void BeginSwipe()
    {
        swipeSign = -swipeSign;
        LastSwipeSign = swipeSign;
        state = State.Swipe;
        swipeStart = Time.time;
        Swipes++;
    }

    // The droplets leave from in front of us, along the view at the hand's height, leaning the way the
    // head's going.
    void Release(PlayerController player)
    {
        released = swipeStart;
        Vector3 flat = Flat(player);
        Vector3 look = Quaternion.AngleAxis(SwipePose(player), Vector3.Cross(Vector3.up, flat)) * flat;
        Vector3 origin = Pivot() + look * (reach * 0.6f);
        float aimUp = -player.RaisedAimPitch * aimInfluence;
        Vector3 inherit = body != null ? body.velocity : Vector3.zero;
        inherit.y = 0f;
        float facing = player.transform.eulerAngles.y;
        var launches = new Vector3[droplets];
        var velocities = new float[droplets * 3];
        var sizes = new int[droplets];
        var visible = new bool[droplets];
        for (int i = 0; i < droplets; i++)
        {
            float k = droplets > 1 ? i / (droplets - 1f) : 0.5f;
            float yawOffset = Mathf.Lerp(-fan, fan, k) * 0.5f + lean * swipeSign + Random.Range(-3f, 3f);
            float up = Mathf.Clamp(swipePitch + Random.Range(-3f, 3f) + aimUp, swipePitchLimits.x, swipePitchLimits.y);
            float speed = Random.Range(dropletSpeed.x, dropletSpeed.y);
            Vector3 launch = Quaternion.Euler(-up, facing + yawOffset, 0f) * Vector3.forward * (speed * swipePace);
            launches[i] = launch;
            velocities[i * 3] = launch.x; velocities[i * 3 + 1] = launch.y; velocities[i * 3 + 2] = launch.z;
            sizes[i] = swipeSplat;
            visible[i] = true;
            Fire(origin, launch, inherit, swipeSplat, player.Team, player.OwnerId, true, swipeDamage, swipeHits);
        }
        LastSwipe = launches;
        NetGameManager.Instance?.BroadcastShots(player, origin, inherit, velocities, sizes, visible);
    }

    void Fire(Vector3 from, Vector3 launch, Vector3 inherit, int size, int team, ulong ownerId, bool authoritative, DamageFalloff damage, HashSet<Object> hits)
    {
        ProjectileManager.Fire(from, launch, size, team, true, authoritative, ownerId, 0f,
                               ballistics: paced ?? ballistics, falloff: damage, inherit: inherit, source: Source, hitGroup: hits);
    }

    // Their swipe, visual-only here, the swing played from where the droplets leave (alternating
    // like theirs).
    public override void Replay(ProjectileSpawnData d)
    {
        if (d.velocities == null || d.splashSizes == null) return;
        Vector3 inherit = d.inherit != null ? (Vector3)d.inherit : Vector3.zero;
        int n = Mathf.Min(d.splashSizes.Length, d.velocities.Length / 3);
        if (!local && n > 0)
        {
            BeginSwipe();
            swipeStart = Time.time - swipeTime * releaseAt;
        }
        for (int i = 0; i < n; i++)
            Fire(d.origin, new Vector3(d.velocities[i * 3], d.velocities[i * 3 + 1], d.velocities[i * 3 + 2]), inherit, d.splashSizes[i], d.team, d.shooterSteamId, false, null, null);
    }

    // A row of splats across the head, onto whatever's under it.
    void Paint(PlayerController player)
    {
        Vector3 centre = head != null ? head.position : transform.position;
        Vector3 across = Vector3.Cross(Vector3.up, BodyForward(player));
        float half = headWidth * 0.5f;
        for (int i = 0; i < brushSplatPoints; i++)
        {
            float k = brushSplatPoints > 1 ? i / (brushSplatPoints - 1f) : 0.5f;
            Vector3 at = centre + across * Mathf.Lerp(-half * 0.6f, half * 0.6f, k);
            if (!Physics.Raycast(at + Vector3.up, Vector3.down, out RaycastHit hit, 1f + headThickness * 4f, PhysicsLayers.Environment, QueryTriggerInteraction.Ignore)) continue;
            SurfaceInkManager ink = hit.collider.GetComponent<SurfaceInkManager>();
            if (ink != null) ink.SplatAt(hit, brushSplatSize, player.Team);
        }
    }

    // Enemies (and their subs) the head meets: hit once as they come under it.
    void RunOver(PlayerController player)
    {
        Vector3 centre = head != null ? head.position : transform.position;
        var extents = new Vector3(headWidth * 0.5f, headThickness + 0.4f, 0.4f);
        int n = Physics.OverlapBoxNonAlloc(centre, extents, overlaps, Quaternion.LookRotation(BodyForward(player)), LayerMask.GetMask("Hitbox"), QueryTriggerInteraction.Collide);
        var now = new HashSet<Object>();
        for (int i = 0; i < n; i++)
        {
            PlayerHitbox hitbox = overlaps[i].GetComponentInParent<PlayerHitbox>();
            if (hitbox != null)
            {
                if (hitbox.Team == player.Team) continue;
                now.Add(hitbox);
                if (!underHead.Contains(hitbox)) hitbox.TakeDamage(brushDamage, player.Team, player.OwnerId, Source);
                continue;
            }
            SubDevice sub = overlaps[i].GetComponentInParent<SubDevice>();
            if (sub != null && sub.Team != player.Team)
            {
                now.Add(sub);
                if (!underHead.Contains(sub)) sub.TakeDamage(brushDamage, player.Team, player.OwnerId);
            }
        }
        underHead.Clear();
        underHead.UnionWith(now);
    }

    void LateUpdate()
    {
        PlayerController player = Owner;
        if (player == null) return;
        // Idle (not attacking or brushing; for remotes, not down): held up at our side.
        // Held in the air (or out of ink): out in front, ready to brush.
        bool idle = !Down && !(local && state == State.Held);
        float targetPitch = idle ? idlePitch : carryPitch, targetShift = 0f, targetFold = idle ? 1f : 0f, targetYaw = 0f;
        bool snap = false;
        if (state == State.Swipe) // ours, or theirs replayed: across in front, from one side to the other, where they look
        {
            float t = Mathf.Clamp01((Time.time - swipeStart) / swipeTime);
            yawOff = Mathf.Lerp(-swipeArc, swipeArc, Mathf.SmoothStep(0f, 1f, t)) * 0.5f * swipeSign;
            targetPitch = SwipePose(player);
            fold = Mathf.Lerp(fold, 0f, Mathf.Clamp01(t * 3f));
            snap = true;
        }
        else if (Down)
        {
            targetPitch = GroundPitch(player);
            targetShift = 1f;
        }
        float ease = 1f - Mathf.Exp(-poseSpeed * Time.deltaTime);
        pitch = Mathf.Lerp(pitch, targetPitch, snap ? Mathf.Max(ease, 0.5f) : ease);
        shift = Mathf.Lerp(shift, targetShift, ease);
        if (!snap) { yawOff = Mathf.Lerp(yawOff, targetYaw, ease); fold = Mathf.Lerp(fold, targetFold, ease); }
        transform.localPosition = homePos + brushShift * shift + idleOffset * fold;
        transform.rotation = Quaternion.Euler(pitch, BodyYaw(player) + yawOff, 0f); // along the body (which faces the view while swiping)
    }

    // A swipe's pitch (degrees down): the view's, within limits.
    float SwipePose(PlayerController player) => Mathf.Clamp(player.CameraPitch, -swipePoseLimits.y, -swipePoseLimits.x);
    public float SwipePitchNow => pitch; // tests

    // Tipped down until the head sits on the floor in front.
    float GroundPitch(PlayerController player)
    {
        Vector3 pivot = Pivot();
        Vector3 ahead = pivot + BodyForward(player) * reach * 0.9f;
        float floor = Physics.Raycast(ahead + Vector3.up, Vector3.down, out RaycastHit hit, 4f, PhysicsLayers.Environment, QueryTriggerInteraction.Ignore)
            ? hit.point.y : player.transform.position.y;
        float sin = (pivot.y + drop - floor - headThickness) / Mathf.Max(reach, 0.1f);
        return Mathf.Asin(Mathf.Clamp(sin, -0.5f, 0.95f)) * Mathf.Rad2Deg;
    }

    Vector3 Pivot() => transform.parent != null ? transform.parent.TransformPoint(homePos + brushShift * shift + idleOffset * fold) : transform.position;

    // isGrounded alone drops out on frames that barely move us down: also standing on something
    // just under the feet and not rising (a jump is).
    bool OnGround(PlayerController player)
    {
        if (player.IsGrounded) return true;
        if (body != null && body.velocity.y > 0.5f) return false;
        return Physics.Raycast(player.transform.position + Vector3.up * 0.1f, Vector3.down, 0.25f, PhysicsLayers.Environment, QueryTriggerInteraction.Ignore);
    }

    float BodyYaw(PlayerController player) => transform.parent != null ? transform.parent.eulerAngles.y : player.transform.eulerAngles.y;
    Vector3 BodyForward(PlayerController player) => Quaternion.Euler(0f, BodyYaw(player), 0f) * Vector3.forward;

    static Vector3 Flat(PlayerController player)
    {
        Vector3 f = player.transform.forward;
        f.y = 0f;
        return f.sqrMagnitude > 1e-6f ? f.normalized : Vector3.forward;
    }
}

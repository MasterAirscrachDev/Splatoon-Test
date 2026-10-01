using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

public class PlayerController : MonoBehaviour
{
    [SerializeField] Transform playerCamera = null;
    [SerializeField] int team = 0;
    public int Team => team;
    [SerializeField] float mouseSensitivity = 3.5f;
    [Header("Movement Settings")]
    [SerializeField] float gravity = -13.0f;
    [SerializeField] float jump = 8.0f;
    [SerializeField][Range(0.0f, 0.5f)] float moveSmoothTime = 0.3f;
    [SerializeField][Range(0.0f, 0.5f)] float mouseSmoothTime = 0.03f;
    [SerializeField] float moveSpeed = 6.0f;
    [SerializeField] float swimSpeed = 10.0f;
    [SerializeField] bool lockCursor = true, gyro;
    [SerializeField] float viewmodelSwitchSpeed = 12f;
    [SerializeField] Vector3 velocity;
    [SerializeField] GameObject ViewmodelPlayer, ViewmodelSquid, SquidTrail, InkTankScaler;
    [SerializeField] GameObject InkExitSplashPrefab;
    [SerializeField] GameObject swimSplashParticlesPrefab;
    [Header("Ink")]
    [SerializeField] float inkRechargeRate = 0.1f;
    [SerializeField] float inkRechargeRateSquid = 0.55f;
    [Header("Network")]
    [SerializeField] PlayerMode playerMode = PlayerMode.Client;
    [SerializeField] UIController uiController;
    public bool IsLocalPlayer => playerMode == PlayerMode.Client;
    int surfaceTeam = 0;
    bool swimMode;
    bool isDead;
    float inkLevel = 1f;
    public float InkLevel => inkLevel;
    public bool IsSquid => swimMode;
    public bool IsDead => isDead;
    // Blocks re-engaging a climb for a brief window right after a wall-jump, so the sensor
    // (which can keep detecting the wall for the whole jump arc) can't immediately re-attach.
    bool InWallJumpGrace => Time.time < wallJumpGraceUntil;
    // On a wall, only actually climbing counts as being in its ink. Mere sensor contact
    // (isClimbing) also covered falling past an own-ink wall, e.g. after popping off the top
    // of the ink or wall-jumping, which re-hid the squid mid-air. effectivelyClimbing is also
    // the networked climbing flag on remote copies.
    public bool IsInInk => swimMode && (surfaceTeam == team || effectivelyClimbing);
    // Steepest slope that counts as floor rather than wall. Deliberately NOT the controller's
    // live slopeLimit: that's raised to 90° while climbing, and WallClimbSensor classifies
    // walls against this value — reading the live one made every wall look "too shallow" the
    // moment climbing started, so climbing dropped every few frames, re-engaged, and repeated.
    [SerializeField] float walkableSlopeLimit = 45f;
    public float SlopeLimit => walkableSlopeLimit;

    // ── Scripted input + read-only state, for test harnesses (ClimbTestRunner) and debug UI ──
    // When ScriptedInput is set it replaces hardware input entirely (move/look/swim, and the
    // hardware jump binding is ignored — call PressJump instead).
    public ScriptedPlayerInput ScriptedInput { get; set; }
    public void PressJump() => Jump();
    public bool IsClimbing => effectivelyClimbing;   // actually moving in climb mode this frame
    public bool HasWallContact => isClimbing;        // sensor reports an own-ink wall in range
    public Vector3 ClimbNormal => climbNormal;
    public bool IsGrounded => controller != null && controller.enabled && controller.isGrounded;
    public bool ControllerEnabled => controller != null && controller.enabled;
    public Vector3 BodyCenter => transform.position + (controller != null ? controller.center : Vector3.zero);
    public Transform SquidVisual => ViewmodelSquid != null ? ViewmodelSquid.transform : null;
    public int SurfaceTeam => surfaceTeam;            // team of the ink directly below (0 if none)
    public float VerticalVelocity => velocityY;
    public float CurrentSpeed => realSpeed;
    public CollisionFlags LastCollisionFlags => controller != null ? controller.collisionFlags : CollisionFlags.None;
    float realSpeed, airSpeed, cameraPitch = 0.0f, velocityY = 0.0f;
    CharacterController controller = null;
    ControlLayer input;
    [SerializeField] Material mat;
    bool ownsMat; // mat was replaced by a runtime instance (local player) and must be destroyed with us
    [SerializeField] float groundStickForce = 3f; // keeps the capsule pressed onto slopes instead of separating/reacquiring each frame
    [SerializeField] float climbSlideCap = 3f; // max gravity slide speed while climbing — kept well below realSpeed so gravity slows a climb, never fully arrests it
    [SerializeField] float climbJumpScale = 0.4f; // wall kick-off strength while ejecting from a climb (jump pressed while descending), as a fraction of the normal jump impulse
    [Header("Climb speed")]
    [SerializeField] float climbSpeedFactor = 0.8f;   // base climb speed as a fraction of own-ink swim speed
    [SerializeField] float climbBoostPerJump = 0.35f; // each jump while climbing adds this much boost (0..1); full boost = full own-ink swim speed
    [SerializeField] float climbBoostDecay = 0.7f;    // boost lost per second, so it has to be kept up by repeated jumps
    float climbBoost; // 0..1, lerps climb speed from climbSpeedFactor up to full swim speed
    [Header("Popping off a wall")]
    [SerializeField] float climbPopForward = 2.5f;    // push toward the wall's side when the wall (or our ink on it) ends: carries the squid over a top lip
    [SerializeField] float climbPopAirControl = 3f;   // horizontal input speed while airborne after a pop, instead of instantly swimming at full speed
    [SerializeField] float climbPopNoSwimTime = 0.2f; // after popping off, can't re-grab a wall for this long
    [SerializeField] float climbPopInkEdgeLift = 0.25f; // fraction of upward climb speed kept when popping off the edge of our ink
    [SerializeField] float climbPopTopLift = 0.5f;      // fraction of upward climb speed kept when popping off the top of a wall
    bool lostWallAtInkEdge; // set when climb contact ends: true = edge of our ink, false = the wall itself ended
    [SerializeField] float wallJumpGraceDuration = 0.3f; // how long after a wall-jump the player is forced out of wall-ink/climb state
    float wallJumpGraceUntil = -1f;
    bool isClimbing;
    bool wasClimbing;
    int missedWallFrames;
    [SerializeField] int missedWallGrace = 3; // consecutive frames the wall can go undetected before actually dropping climb contact
    [SerializeField] float climbNormalSmoothing = 15f; // how fast the squid/trail visuals ease toward a newly-detected wall normal
    Vector3 climbNormal;       // raw wall normal from WallClimbSensor — drives movement
    Vector3 visualClimbNormal; // eased copy — drives the squid model and trail orientation only
    [SerializeField] float climbEngageDot = 0.3f; // how directly input must push into a wall to start climbing it (dot of move dir vs -normal)
    // World-space direction the player is trying to move this frame: along the wall while
    // climbing, horizontal otherwise. WallClimbSensor uses it to prefer the wall being moved
    // into (inner corners).
    Vector3 climbIntent;
    public Vector3 ClimbIntent => climbIntent;
    // After popping off a wall (top lip or edge of our ink), no climb can start before this time.
    float regrabBlockedUntil = -1f;
    // Horizontal velocity carried through the air after a pop-off (cleared on landing or
    // re-grabbing), so leaving a wall keeps its momentum instead of switching straight to
    // input-driven swimming.
    Vector3 airMomentum;
    Vector3 lastClimbVelocity; // last frame's climb velocity along the wall (excludes the pull into it)
    SurfaceInkManager climbInk; // set by WallClimbSensor alongside climbNormal; used to indent the wall's normal map while climbing
    Vector2 climbInkUV;
    bool prevInOwnInk;
    float lastExitSplatTime = -1f;
    float lastEnterSplatTime = -1f;
    [SerializeField] float fallSplashMinHeight = 0.5f; // minimum fall distance to trigger a landing-in-ink splash
    float lastGroundedY;
    bool wasGroundedPrev;
    bool effectivelyClimbing;
    SurfaceInkManager groundInk; // cached each frame by GetInkTeam; also used to indent the swim trail
    Vector2 groundInkUV;
    Vector3 groundNormal = Vector3.up; // cached each frame by GetInkTeam; feeds slope-aligned squid orientation
    Vector2 currentDir = Vector2.zero, currentDirVelocity = Vector2.zero, currentMouseDelta = Vector2.zero, currentMouseDeltaVelocity = Vector2.zero, targetDir;

    // Remote (Network mode) interpolation targets, fed by ApplyNetState.
    [SerializeField] float netLerpSpeed = 14f;
    Vector3 netTargetPos;
    float netTargetYaw, netCamPitch;
    bool netInitialised;

    // State broadcast: snapshot built on main thread each frame, sent from OnNetTick (off-thread).
    ulong localSteamId;
    PlayerStateData cachedNetState;
    Color teamColor;

    void Start()
    {
        controller = GetComponent<CharacterController>();

        // Only the local client takes hardware input or owns the cursor / HUD.
        // (NetGameManager sets playerMode right after Instantiate, before Start runs.)
        if (playerMode == PlayerMode.Client)
        {
            input = new ControlLayer();
            input.Enable();
            input.Movement.Jump.performed += ctx => { if (ScriptedInput == null) Jump(); };
            input.Movement.Debug.performed += ctx => TestCheckScores();
            if (lockCursor){ Cursor.lockState = CursorLockMode.Locked; Cursor.visible = false; }
            if (uiController == null) uiController = FindFirstObjectByType<UIController>();
            // Fade a per-player copy, never the shared asset — writing mat.color directly
            // edited Player.mat itself, fading every player using it (remotes included) and
            // permanently changing the asset when run in the editor.
            if (mat != null) { mat = InstanceMaterial(mat); ownsMat = true; }
            localSteamId = SteamGlobal.steamID.Value;
            SteamGlobal.OnNetTick += NetTickBroadcast;
        }
        else
        {
            Destroy(playerCamera.gameObject); // remote players don't own a camera
        }

        // Resolve team colour for both modes (team is set by NetGameManager before Start runs).
        NetGameManager gm = FindFirstObjectByType<NetGameManager>();
        if (gm != null) teamColor = team == 1 ? gm.AlphaTeam : gm.BetaTeam;
        ApplyTeamColor();
    }

    // Swaps every child renderer slot using `shared` for one new instance, and returns it.
    Material InstanceMaterial(Material shared)
    {
        Material inst = new Material(shared);
        foreach (Renderer r in GetComponentsInChildren<Renderer>(true))
        {
            Material[] mats = r.sharedMaterials;
            bool changed = false;
            for (int i = 0; i < mats.Length; i++)
                if (mats[i] == shared) { mats[i] = inst; changed = true; }
            if (changed) r.sharedMaterials = mats;
        }
        return inst;
    }

    void ApplyTeamColor()
    {
        // 3D backpack tank
        if (InkTankScaler != null)
        {
            Renderer r = InkTankScaler.GetComponentInChildren<Renderer>();
            if (r != null) r.material.color = teamColor;
        }
        // Swim trail — covers both mesh renderers and particle systems
        if (SquidTrail != null)
        {
            Renderer tr = SquidTrail.GetComponentInChildren<Renderer>(true);
            if (tr != null) tr.material.color = teamColor;
            ParticleSystem ps = SquidTrail.GetComponentInChildren<ParticleSystem>(true);
            if (ps != null) { var m = ps.main; m.startColor = teamColor; }
        }
        // Squid viewmodel body
        if (ViewmodelSquid != null && ViewmodelSquid.transform.childCount > 0)
        {
            Renderer r = ViewmodelSquid.transform.GetChild(0).GetComponent<Renderer>();
            if (r != null) r.material.color = teamColor;
        }
        // HUD tank
        if (uiController != null) uiController.SetTeamColor(teamColor);
    }

    void Update()
    {
        if(playerMode == PlayerMode.Client) {
            UpdateMouseLook();
            UpdateMovement();
            float distance = Vector3.Distance(Camera.main.transform.position, transform.position);
            mat.color = distance < 2
                ? new Color(1, 1, 1, Mathf.Clamp(distance / 2, 0, 1))
                : new Color(1, 1, 1, 1);
            cachedNetState = GetNetState(localSteamId, 0); // tick is stamped in NetTickBroadcast
        }
        else if(playerMode == PlayerMode.Network) {
            UpdateNetInterpolation();
        }
        else
        {
            
        }
        
        GetInkTeam();
        UpdateInk();
        UpdateViewmodels();
        UpdateSquidTrail();
    }

    // Smoothly move a remote player toward the last state we received over the network.
    void UpdateNetInterpolation()
    {
        if (playerMode != PlayerMode.Network) return;
        if (!netInitialised) return;

        transform.position = Vector3.Lerp(transform.position, netTargetPos, Time.deltaTime * netLerpSpeed);
        Quaternion targetRot = Quaternion.Euler(0f, netTargetYaw, 0f);
        transform.rotation = Quaternion.Slerp(transform.rotation, targetRot, Time.deltaTime * netLerpSpeed);
        if (playerCamera != null) playerCamera.localEulerAngles = Vector3.right * netCamPitch;
    }

    void UpdateMouseLook()
    {
        if (playerMode != PlayerMode.Client) return;
        Vector2 targetMouseDelta = ScriptedInput != null ? ScriptedInput.look : input.Movement.Look.ReadValue<Vector2>();
        currentMouseDelta = Vector2.SmoothDamp(currentMouseDelta, targetMouseDelta, ref currentMouseDeltaVelocity, mouseSmoothTime);
        cameraPitch -= currentMouseDelta.y * mouseSensitivity;
        cameraPitch = Mathf.Clamp(cameraPitch, -70.0f, 80.0f);
        playerCamera.localEulerAngles = Vector3.right * cameraPitch;
        transform.Rotate(Vector3.up * currentMouseDelta.x * mouseSensitivity);
    }
    void UpdateMovement()
    {
        if (playerMode != PlayerMode.Client) return;
        if (isDead) return;

        float prevHeight = controller.height;
        float prevRadius = controller.radius;
        bool wasClimbingWall = wasClimbing; // captured before this frame's climbing state is recomputed below
        bool swimHeld = ScriptedInput != null ? ScriptedInput.swim : input.Movement.Squidmode.ReadValue<float>() != 0;
        if(swimHeld){
            swimMode = true;
            // stepOffset must drop BEFORE the capsule shrinks. Unity requires
            // stepOffset <= height + radius*2; the standing 0.3 against the squid 0.1 + 0.1*2
            // sits exactly on that limit and float rounding pushes it over, which makes Unity
            // reject the controller ("Step Offset must be less or equal to...") and leave it
            // inactive — every Move() after that fails until Respawn re-enables it.
            controller.stepOffset = 0f;
            controller.height = 0.1f;
            controller.radius = 0.1f;
        }
        else{
            swimMode = false;
            isClimbing = false; // dismount wall when leaving squid mode
            controller.height = 1.92f;
            controller.radius = 0.5f;
        }
        if (controller.height != prevHeight)
        {
            // controller.center never changes (the capsule stays centred on the player's
            // origin — the same point the camera and UI anchor to) — so shrinking/growing the
            // capsule around a fixed centre moves its bottom relative to the floor. Nudge the
            // origin once, by exactly half the height delta, so the capsule's bottom stays on
            // the ground across the transition without moving the origin (or viewmodels, which
            // sit at a fixed local offset from it) every frame.
            transform.position += Vector3.up * ((controller.height - prevHeight) / 2f);

            // Radius grows a lot too (0.1 -> 0.5). If that happens while still flush against a
            // wall (leaving swim mode mid-climb), the much wider capsule spawns overlapping the
            // wall and CharacterController's own push-out resolution can leave the player
            // visibly jammed/stuck instead of cleanly separated. Push away from the wall we were
            // just on by the radius growth (plus a skin-width margin) to avoid that overlap.
            if (wasClimbingWall && controller.radius > prevRadius)
                transform.position += climbNormal * (controller.radius - prevRadius + controller.skinWidth);

            // Auto Sync Transforms is off in this project, so the CharacterController's physics
            // body doesn't see direct transform writes on its own: the Move() later this frame
            // would start from the old position and silently undo both nudges above (entering
            // swim form then dropped the squid ~0.9m through the air instead of re-seating it).
            Physics.SyncTransforms();
        }
        bool enteredSwimModeThisFrame = swimMode && controller.height != prevHeight;

        // Track grounded transitions for the "entered ink from a fall" splash trigger below.
        bool grounded = controller.isGrounded;
        bool justLanded = grounded && !wasGroundedPrev;
        float fallHeight = justLanded ? lastGroundedY - transform.position.y : 0f;
        if (grounded) lastGroundedY = transform.position.y;
        wasGroundedPrev = grounded;

        gameObject.layer = swimMode ? 11 : 7;
        realSpeed = 6;
        if(surfaceTeam != 0){ // if standing on ink
            bool inOwnInk = surfaceTeam == team;
            if (swimMode)
            {
                realSpeed = inOwnInk ? swimSpeed + 2 : swimSpeed / 2;
            }
            else
            {
                realSpeed = inOwnInk ? moveSpeed : moveSpeed - 2;
            }
        }
        else{
            realSpeed = swimMode ? swimSpeed / 4 : moveSpeed;
        }
        targetDir = ScriptedInput != null ? ScriptedInput.move : input.Movement.Move.ReadValue<Vector2>(); targetDir.Normalize();
        currentDir = Vector2.SmoothDamp(currentDir, targetDir, ref currentDirVelocity, moveSmoothTime);

        // Eject from climbing when we've descended to the floor at the base of a wall.
        bool wantsToClimb = currentDir.y > 0.01f;
        bool atWallBase = controller.isGrounded && !wantsToClimb;
        // Entering a climb requires actively moving *into* the wall: the horizontal input
        // direction has to point against the wall's normal. Checking only "forward is held"
        // also engaged when the wall was behind the player. Swimming forward off a ledge
        // with an inked wall below grabbed that wall as the player fell past it, and forward
        // then meant "up", pulling them back up it. Once already
        // climbing, atWallBase alone still governs staying attached, so climbing down or
        // passively sliding (climbSlideCap) doesn't require continuously holding up.
        // inWallJumpGrace forces a brief window of "definitely not climbing" right after a
        // wall-jump so the sensor can't immediately re-engage it before the jump carries the
        // player away.
        Vector3 flatMove = transform.forward * currentDir.y + transform.right * currentDir.x;
        bool pushingIntoWall = isClimbing && Vector3.Dot(flatMove, -climbNormal) > climbEngageDot;
        bool regrabBlocked = Time.time < regrabBlockedUntil; // brief no-swim window after a pop-off
        bool canEngageClimb = !InWallJumpGrace && (wasClimbing ? !atWallBase : pushingIntoWall && !regrabBlocked);
        // isClimbing already means "own-team wall right here" — team filtering happens at
        // acquisition in WallClimbSensor now, nothing left to check here.
        bool climbing = swimMode && isClimbing && canEngageClimb;
        effectivelyClimbing = climbing;
        if (climbing)
        {
            // Reset any accumulated falling velocity on the first frame of wall contact.
            if (!wasClimbing) { velocityY = 0f; airMomentum = Vector3.zero; }
            wasClimbing = true;

            // The speed picked above comes from the ink directly *below* the player, and on a
            // wall there's nothing below, so base it on own-ink swim speed (the sensor only
            // reports own-team walls): climbSpeedFactor of it normally, up to all of it while
            // jump-boosted (see Jump).
            climbBoost = Mathf.Max(0f, climbBoost - climbBoostDecay * Time.deltaTime);
            realSpeed = (swimSpeed + 2) * Mathf.Lerp(climbSpeedFactor, 1f, climbBoost);

            controller.slopeLimit = 90f;
            controller.stepOffset = 0f;

            velocityY += gravity * 0.1f * Time.deltaTime;
            // Capped well below realSpeed, not at it — the cap used to equal realSpeed, so
            // once gravity fully accumulated (a few seconds of climbing) it exactly cancelled
            // a full-up climb input (net wallUp speed = realSpeed + velocityY = 0), making
            // climbing stop dead instead of merely slowing.
            velocityY  = Mathf.Max(velocityY, -climbSlideCap);

            Vector3 wallRight = Vector3.Cross(Vector3.up, climbNormal).normalized;
            Vector3 wallUp    = Vector3.Cross(climbNormal, wallRight).normalized;
            climbIntent = wallUp * currentDir.y - wallRight * currentDir.x;
            lastClimbVelocity = wallUp    * (currentDir.y * realSpeed + velocityY)
                              - wallRight *  currentDir.x * realSpeed;
            velocity = lastClimbVelocity - climbNormal * 2f;
            controller.Move(velocity * Time.deltaTime);
            // No forced yaw/camera rotation here — WallClimbSensor re-detects the closest wall
            // fresh every frame regardless of which way the player is facing, so movement and
            // visuals (UpdateViewmodels/UpdateSquidTrail) can just read the current climbNormal
            // directly without needing the player's actual facing to track the wall at all.
        }
        else
        {
            if (wasClimbing)
            {
                // The wall dropped out from under us (we climbed past its top lip, or past the
                // edge of our ink on it) rather than us leaving it on purpose (jump, reaching
                // the floor, leaving swim form): pop off, keeping the climb's momentum.
                bool lostWall = swimMode && !isClimbing && !InWallJumpGrace;
                if (lostWall) PopOffWall();
                // Otherwise discard any negative wall-slide velocity so it doesn't carry into freefall.
                else velocityY = Mathf.Max(velocityY, 0f);
            }
            wasClimbing = false;

            controller.slopeLimit = walkableSlopeLimit;
            controller.stepOffset = swimMode ? 0f : 0.3f; // disable step logic in squid mode to avoid "bouncing" off walls
            velocityY += (gravity * 3) * Time.deltaTime;
            if (controller.isGrounded)
            {
                airSpeed = realSpeed;
                airMomentum = Vector3.zero;
                // A small constant downward "stick" instead of zeroing velocityY keeps the
                // capsule pressed onto sloped ground (see groundStickForce). Guarded on
                // velocityY <= 0 so it never stomps a jump impulse applied earlier this same
                // frame — Jump()'s input callback fires before this runs, and the character
                // hasn't physically left the ground yet by the time this check happens, so an
                // unguarded reset here cancelled every jump before Move() could apply it.
                if (velocityY <= 0f) velocityY = -groundStickForce;
            }

            if (velocityY > 10){ velocityY = 10; }
            float hSpeed = controller.isGrounded ? realSpeed : airSpeed;
            climbIntent = flatMove;
            Vector3 move = flatMove * hSpeed;
            // On walkable ground, move along the surface instead of horizontally. A purely
            // horizontal move steps off a downhill slope every frame (12 m/s down 30° needs ~7 m/s
            // of drop, far more than groundStickForce), so the squid bounced down slopes airborne,
            // losing its ink speed and camouflage on every hop.
            if (controller.isGrounded && velocityY <= 0f && groundNormal.y >= Mathf.Cos(walkableSlopeLimit * Mathf.Deg2Rad))
                move = Vector3.ProjectOnPlane(move, groundNormal).normalized * move.magnitude;
            velocity = move + airMomentum + Vector3.up * velocityY;
            controller.Move(velocity * Time.deltaTime);
        }

        // Spawn a splash when transitioning into or out of own ink while swimming. Runs every
        // frame regardless of climbing state — this used to sit after the climbing branch's
        // early return, so prevInOwnInk froze for the whole duration of a climb and only
        // caught up the instant climbing ended, firing the exit splash wherever that happened
        // to be (e.g. on landing somewhere unrelated after a wall-jump) instead of at the wall.
        // Actually climbing (not just sensor contact): a wall-jump or ink-edge pop-off counts as
        // leaving the ink immediately, even if the wall stays in sensor range.
        bool onWallInk = effectivelyClimbing;
        bool inOwnInkNow = swimMode && team != 0 && (surfaceTeam == team || onWallInk);
        Vector3 inkNormal = onWallInk ? climbNormal : groundNormal;

        // Deliberately NOT "any transition into ink" (prevInOwnInk-based) — that fired every
        // time isClimbing flickered true on a wall, which is continuously, the whole time
        // you're pressed against one. Only two specific triggers: entering swim mode while
        // already standing on own ink, or landing on own ink from a meaningful fall. Neither
        // can be satisfied by wall-climbing at all.
        bool enteredSwimModeOnInk  = enteredSwimModeThisFrame && surfaceTeam == team;
        bool landedOnInkFromHeight = justLanded && surfaceTeam == team && fallHeight > fallSplashMinHeight;
        if ((enteredSwimModeOnInk || landedOnInkFromHeight) && Time.time - lastEnterSplatTime >= 0.2f)
        {
            lastEnterSplatTime = Time.time;
            InkParticles.Spawn(swimSplashParticlesPrefab, transform.position + controller.center,
                Quaternion.FromToRotation(Vector3.up, groundNormal), teamColor);
        }

        // The && swimMode guard prevents triggering when the player leaves swim mode while still on ink.
        if (prevInOwnInk && !inOwnInkNow && swimMode && InkExitSplashPrefab != null && Time.time - lastExitSplatTime >= 0.2f)
        {
            lastExitSplatTime = Time.time;
            GameObject splash = Instantiate(InkExitSplashPrefab, transform.position + controller.center, Quaternion.FromToRotation(Vector3.up, inkNormal));
            splash.GetComponent<InkEmitter>().Setup(team, 3, true);
        }
        prevInOwnInk = inOwnInkNow;

        if(transform.position.y < -10){
            Respawn();
        }
    }

    [Header("Respawn")]
    [SerializeField] Vector3 spawnPoint = new Vector3(0, 3, 0); // fallback if no tagged spawn exists

    void HideModels()
    {
        if (ViewmodelPlayer != null) ViewmodelPlayer.SetActive(false);
        if (ViewmodelSquid  != null) ViewmodelSquid.SetActive(false);
        if (SquidTrail      != null) SquidTrail.SetActive(false);
    }

    void ShowModels()
    {
        if (ViewmodelPlayer != null) ViewmodelPlayer.SetActive(true);
        if (ViewmodelSquid  != null) ViewmodelSquid.SetActive(true);
    }

    public void OnDeath()
    {
        if (isDead || playerMode != PlayerMode.Client) return;
        isDead   = true;
        swimMode = true;
        velocity = Vector3.zero;
        velocityY = 0f;
        HideModels();
        StartCoroutine(RespawnAfterDelay(5f));
    }

    System.Collections.IEnumerator RespawnAfterDelay(float delay)
    {
        yield return new WaitForSeconds(delay);
        Respawn();
    }

    public void Respawn()
    {
        string tag = team == 1 ? "AlphaSpawn" : "BetaSpawn";
        GameObject[] pts = GameObject.FindGameObjectsWithTag(tag);
        Vector3 pos = pts.Length > 0
            ? pts[Random.Range(0, pts.Length)].transform.position
            : spawnPoint;
        PlaceAt(pos, transform.eulerAngles.y);
        isDead = false;
        ShowModels();

        if (playerMode == PlayerMode.Client)
            SteamGlobal.SendAllData((ushort)NetMsg.Teleport, new TeleportData
            {
                steamId  = localSteamId,
                position = pos,
                bodyYaw  = transform.eulerAngles.y
            });
    }

    // Instantly moves the local player (feet at `feetPos`, facing `yaw`) and clears all motion
    // and climb state, back in standing form. Shared by Respawn and test harnesses.
    public void PlaceAt(Vector3 feetPos, float yaw)
    {
        // try/finally so nothing between disabling and re-enabling the controller can leave
        // it permanently disabled with no other code path to recover it.
        controller.enabled = false;
        try
        {
            swimMode = false;
            controller.height = 1.92f;
            controller.radius = 0.5f;
            transform.SetPositionAndRotation(feetPos, Quaternion.Euler(0f, yaw, 0f));
        }
        finally
        {
            controller.enabled = true;
        }
        velocityY  = 0f;
        velocity   = Vector3.zero;
        currentDir = currentDirVelocity = Vector2.zero;
        isClimbing = false;
        wasClimbing = false;
        effectivelyClimbing = false;
        missedWallFrames = 0;
        regrabBlockedUntil = -1f;
        airMomentum = Vector3.zero;
        climbBoost = 0f;
        wallJumpGraceUntil = -1f;
        prevInOwnInk = false;
        wasGroundedPrev = false;
        lastGroundedY = feetPos.y;
    }

    // Applied to a remote (Network mode) copy on receipt of a Teleport message.
    // Bypasses the usual position lerp so respawns snap instantly instead of sliding.
    public void ApplyTeleport(Vector3 pos, float yaw)
    {
        if (playerMode != PlayerMode.Network) return;
        netTargetPos = pos;
        netTargetYaw = yaw;
        transform.position = pos;
        transform.rotation = Quaternion.Euler(0f, yaw, 0f);
    }

    // ── Networking API ─────────────────────────────────────────────────────
    // NetGameManager configures spawned entities and drives state in/out through these.
    public void SetPlayerMode(PlayerMode mode) => playerMode = mode;
    public void SetTeam(int t) => team = t;

    // Snapshot of this (local) player's state to broadcast to other clients.
    public PlayerStateData GetNetState(ulong steamId, uint tick)
    {
        return new PlayerStateData
        {
            steamId  = steamId,
            position = transform.position,
            bodyYaw  = transform.eulerAngles.y,
            camPitch = cameraPitch,
            moveDir  = new Vector2(currentDir.x, currentDir.y),
            team     = team,
            swimMode = swimMode,
            climbing = effectivelyClimbing,
            dead     = isDead,
            tick     = tick
        };
    }

    // Apply a received snapshot to this remote player. Interpolation happens in Update.
    public void ApplyNetState(PlayerStateData s)
    {
        if (playerMode != PlayerMode.Network) return;

        netTargetPos = s.position;
        netTargetYaw = s.bodyYaw;
        netCamPitch  = s.camPitch;
        currentDir   = s.moveDir;
        team         = s.team;
        swimMode     = s.swimMode;
        isClimbing = s.climbing;
        effectivelyClimbing = s.climbing;
        // surfaceTeam is computed locally every frame by GetInkTeam (runs for remotes too).
        // climbNormal/climbInk are NOT networked — WallClimbSensor keeps running locally for
        // remote entities too (see below) and supplies them independently for rendering.

        if (s.dead != isDead)
        {
            isDead = s.dead;
            if (isDead) HideModels(); else ShowModels();
        }

        // Match the controller capsule to the form (cosmetic only — this capsule never
        // collides, see below). Centre is left untouched, same as the local player.
        if (controller != null)
        {
            controller.height = swimMode ? 0.1f : 1.92f;
            controller.radius = swimMode ? 0.1f : 0.5f;
        }

        if (!netInitialised)
        {
            // Remote entities are driven by interpolation, never by the controller.
            // Disable it so it can't fight our transform writes, and snap to the first
            // received pose so we don't lerp in from the prefab's origin.
            netInitialised = true;
            if (controller != null) controller.enabled = false;
            transform.position = netTargetPos;
            transform.rotation = Quaternion.Euler(0f, netTargetYaw, 0f);
        }
    }

    
    void UpdateViewmodels()
    {
        if (isDead) return;
        if (ViewmodelPlayer == null || ViewmodelSquid == null) return;

        Vector3 playerTarget = swimMode ? new Vector3(1,0,1) : Vector3.one;
        Vector3 squidTarget  = (swimMode && !IsInInk) ? Vector3.one : Vector3.zero;

        ViewmodelPlayer.transform.localScale = Vector3.Lerp(ViewmodelPlayer.transform.localScale, playerTarget, Time.deltaTime * viewmodelSwitchSpeed);
        ViewmodelSquid.transform.localScale  = Vector3.Lerp(ViewmodelSquid.transform.localScale,  squidTarget,  Time.deltaTime * viewmodelSwitchSpeed);

        // Hide player model once fully flattened so it doesn't render at near-zero scale
        ViewmodelPlayer.SetActive(ViewmodelPlayer.transform.localScale.y > 0.05f);

        // Align squid viewmodel with movement direction (mirrors swim trail logic)
        if (swimMode && currentDir.magnitude > 0.05f)
        {
            Vector3 moveDir, upHint;
            if (effectivelyClimbing)
            {
                Vector3 wallRight = Vector3.Cross(Vector3.up, visualClimbNormal).normalized;
                Vector3 wallUp    = Vector3.Cross(visualClimbNormal, wallRight).normalized;
                moveDir = (wallUp * currentDir.y - wallRight * currentDir.x).normalized;
                upHint  = visualClimbNormal;
            }
            else
            {
                // Project onto the slope plane so moveDir and upHint are guaranteed orthogonal.
                // A raw horizontal moveDir paired with a tilted upHint aren't orthogonal, so
                // LookRotation's internal correction produced an inconsistent-looking tilt.
                Vector3 flatDir = (transform.forward * currentDir.y + transform.right * currentDir.x).normalized;
                moveDir = Vector3.ProjectOnPlane(flatDir, groundNormal).normalized;
                upHint  = groundNormal; // tilt the squid to match the floor/slope beneath it
            }
            if (moveDir.sqrMagnitude > 0.001f)
                ViewmodelSquid.transform.rotation = Quaternion.LookRotation(moveDir, upHint);
        }
    }
    void UpdateSquidTrail()
    {
        if (isDead || SquidTrail == null) return;
        float moveMag = currentDir.magnitude;
        bool show     = swimMode && IsInInk && moveMag > 0.05f;

        SquidTrail.SetActive(show);
        if (!show) return;

        Vector3 moveDir, upHint;
        if (effectivelyClimbing)
        {
            Vector3 wallRight = Vector3.Cross(Vector3.up, visualClimbNormal).normalized;
            Vector3 wallUp    = Vector3.Cross(visualClimbNormal, wallRight).normalized;
            moveDir = (wallUp * currentDir.y - wallRight * currentDir.x).normalized;
            upHint  = visualClimbNormal; // trail lies flat on wall face

            // Indent the wall's normal map along the climb path too, now that WallClimbSensor
            // passes its own UV/SurfaceInkManager through SetClimbContact.
            if (climbInk != null)
                climbInk.PaintTrailNormal(climbInkUV, 4, 4, 0.01f);
        }
        else
        {
            Vector3 flatDir = (transform.forward * currentDir.y + transform.right * currentDir.x).normalized;
            moveDir = Vector3.ProjectOnPlane(flatDir, groundNormal).normalized; // orthogonal to upHint, see UpdateViewmodels
            upHint  = groundNormal; // trail lies flat on the floor/slope

            // Indent the ink's normal map along the swim path so you can see where players have swum.
            if (groundInk != null)
                groundInk.PaintTrailNormal(groundInkUV, 4, 4, 0.01f);
        }

        if (moveDir.sqrMagnitude > 0.001f)
            SquidTrail.transform.rotation = Quaternion.LookRotation(moveDir, upHint);

        SquidTrail.transform.localScale = Vector3.one * moveMag;
    }

    void UpdateInk()
    {
        if (playerMode != PlayerMode.Client) return;
        // IsInInk, not just the ink below: it also counts own-ink walls while climbing, which
        // the floor-only check missed (no fast recharge on walls).
        float rate = team != 0 && IsInInk ? inkRechargeRateSquid : inkRechargeRate;
        inkLevel = Mathf.Clamp01(inkLevel + rate * Time.deltaTime);

        if (InkTankScaler != null)
        {
            Vector3 s = InkTankScaler.transform.localScale;
            s.y = inkLevel;
            InkTankScaler.transform.localScale = s;
        }
        if (uiController != null)
        {
            uiController.SetSwimMode(swimMode);
            uiController.SetInkLevel(inkLevel);
        }
    }

    public bool ConsumeInk(float amount)
    {
        if (inkLevel < amount || swimMode || ViewmodelPlayer.transform.localScale.y < 0.98f) return false;
        inkLevel -= amount;
        return true;
    }

    // Called every frame by WallClimbSensor. isClimbing being true always means "there is a
    // climbable wall of my own ink right here" — team filtering happens at acquisition in
    // the sensor now, so there's nothing left to check here.
    public void SetClimbContact(Vector3 normal, SurfaceInkManager wallInk, Vector2 wallUV)
    {
        bool wasAlreadyClimbing = isClimbing;
        isClimbing = true;
        missedWallFrames = 0;
        // Movement uses the raw detected normal so it can turn sharp corners at full climb
        // speed: easing it made the movement basis lag behind the wall at outer corners, and
        // at 12 m/s the player flew off tangentially before it caught up. Only the visuals
        // (squid model, trail) use the eased copy, snapped on first contact so a fresh grab
        // doesn't visibly swing in.
        climbNormal = normal;
        visualClimbNormal = wasAlreadyClimbing
            ? Vector3.Slerp(visualClimbNormal, normal, Time.deltaTime * climbNormalSmoothing)
            : normal;
        climbInk = wallInk;
        climbInkUV = wallUV;
    }

    // Leaves the wall carrying the climb's velocity instead of switching straight to swimming:
    // upward speed becomes a hop, sideways speed carries on, plus a small push toward the wall
    // side (over the lip at the top of a wall; at the edge of our ink the wall just stops it).
    // Air control stays limited until landing, and re-grabbing is blocked for a moment so the
    // squid visibly falls back before it can swim into the wall again.
    void PopOffWall()
    {
        // The full climb speed as a hop sends the squid far too high, so it's scaled down,
        // more so at the edge of our ink (the wall carries on above) than at a wall's top lip.
        float lift = lostWallAtInkEdge ? climbPopInkEdgeLift : climbPopTopLift;
        velocityY = Mathf.Max(lastClimbVelocity.y, 0f) * lift;
        airMomentum = Vector3.ProjectOnPlane(lastClimbVelocity, Vector3.up) - climbNormal * climbPopForward;
        airSpeed = climbPopAirControl;
        regrabBlockedUntil = Time.time + climbPopNoSwimTime;
    }

    // A single geometry miss doesn't immediately drop climb contact: the ring can briefly
    // miss while rounding a corner between two ray angles, and without this grace window one
    // such miss flipped isClimbing off and back on, which cascaded into IsInInk (and the squid
    // camouflage hide/show it drives) visibly jittering.
    //
    // atInkEdge (a climbable wall is right there, just not in our ink) skips the grace and
    // drops contact immediately, so UpdateMovement pops the player off (PopOffWall). Team
    // reads are exact, so there's nothing to debounce, and at climb speed the grace window
    // alone carried the squid ~0.6m past its ink.
    // atWallTop (the wall continues just below us) likewise drops contact immediately: waiting
    // out the grace window kept climbing into empty air ~0.45m above the lip before popping.
    public void ClearClimbContact(bool atInkEdge = false, bool atWallTop = false)
    {
        missedWallFrames++;
        if (atInkEdge || atWallTop || missedWallFrames > missedWallGrace)
        {
            if (isClimbing) lostWallAtInkEdge = atInkEdge; // why contact ended, for PopOffWall
            isClimbing = false;
            climbInk = null;
        }
    }

    void OnDestroy()
    {
        if (playerMode == PlayerMode.Client)
            SteamGlobal.OnNetTick -= NetTickBroadcast;
        if (ownsMat && mat != null) Destroy(mat);
    }

    // Fires off the Unity main thread at the network tick rate. Only reads the cached
    // snapshot (a reference captured from the last main-thread frame) and hands it to
    // the transport — no Unity object access here.
    void NetTickBroadcast(uint tick)
    {
        PlayerStateData snap = cachedNetState;
        if (snap == null) return;
        snap.tick = tick;
        // Unreliable: a full snapshot goes out every tick, so a lost one is superseded by the
        // next anyway. Reliable delivery made one dropped packet stall every later snapshot
        // behind its resend (head-of-line blocking), which showed up as remote rubber-banding.
        // One-off events that must arrive (Teleport, Splat, InkReset) stay reliable.
        SteamGlobal.SendAllData((ushort)NetMsg.PlayerState, snap, reliable: false);
    }

    void Jump(){
        if (effectivelyClimbing)
        {
            // Same term the climbing branch itself uses for wallUp's coefficient — negative
            // means we're currently descending the wall (holding down, or just sliding via
            // climbSlideCap with no input at all).
            float wallClimbSpeed = currentDir.y * realSpeed + velocityY;
            if (wallClimbSpeed < 0f)
            {
                // Jumping while descending ejects from the wall entirely — pressing jump on
                // the way down reads as "get me off this wall", not "climb faster".
                velocityY += jump * 2 * climbJumpScale;
                // Counts as immediately leaving the wall's ink — see InWallJumpGrace.
                wallJumpGraceUntil = Time.time + wallJumpGraceDuration;
            }
            else
            {
                // Otherwise (climbing up, or holding still) jump boosts climb speed and keeps us
                // on the wall: each press raises it toward full own-ink swim speed and the boost
                // decays, so spamming jump climbs faster. No wall-jump grace here: we're
                // deliberately staying attached, not leaving.
                climbBoost = Mathf.Min(1f, climbBoost + climbBoostPerJump);
                // Also cancel any accumulated gravity slide, so a boosted climb really reaches
                // full swim speed instead of full speed minus the slide.
                velocityY = Mathf.Max(velocityY, 0f);
            }
            return;
        }
        // Unified to the same signal UpdateMovement uses for gravity/ground resolution —
        // previously Jump() read a separate raycast-derived bool that (like the raycast
        // itself) went stale in swim mode once the capsule wasn't recentring on shrink.
        if (controller.isGrounded || IsInInk) { velocityY += jump * 2; }
    }
    void TestCheckScores(){
        NetGameManager gm = FindFirstObjectByType<NetGameManager>();
        gm.GetScores();
        gm.GetTopDownScores();
    }
    void GetInkTeam(){
        RaycastHit inkHit;
        if (Physics.Raycast(transform.position + controller.center, Vector3.down, out inkHit, (controller.height / 2) + 0.3f)){
            Debug.DrawLine(transform.position + controller.center, inkHit.point, Color.blue);
            groundInk = inkHit.collider.GetComponent<SurfaceInkManager>();
            groundInkUV = inkHit.textureCoord;
            groundNormal = inkHit.normal;
            surfaceTeam = groundInk != null ? groundInk.getSurfaceTeam(groundInkUV) : 0;
        }else if (controller.enabled && controller.isGrounded){
            // Still standing on something the centre ray can't see: the capsule's edge resting
            // on a lip as the player swims off a ledge. Keep the last ink reading. Treating
            // it as "no ink" dropped the speed to the no-ink value for those last grounded
            // frames, and that became the airborne speed, so swimming off a ledge lost all speed.
            Debug.DrawRay(transform.position + controller.center, Vector3.down * ((controller.height / 2) + 0.3f), Color.yellow);
        }else{
            Debug.DrawRay(transform.position + controller.center, Vector3.down * ((controller.height / 2) + 0.3f), Color.red);
            surfaceTeam = 0;
            groundInk = null;
            groundNormal = Vector3.up;
        }
    }
}
public enum PlayerMode
{
    Client, Network
}

// Input values a script feeds a player in place of hardware (see PlayerController.ScriptedInput).
public class ScriptedPlayerInput
{
    public Vector2 move;  // same convention as the Move action: x = strafe, y = forward
    public Vector2 look;  // per-frame mouse delta
    public bool swim;     // held squid/swim mode
}

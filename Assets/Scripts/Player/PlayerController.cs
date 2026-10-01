using System.Collections;
using UnityEngine;

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
    [SerializeField] bool lockCursor = true;
    [SerializeField] float viewmodelSwitchSpeed = 12f;
    [SerializeField] float cameraFormSmoothTime = 0.12f; // camera settle time after a form switch
    [SerializeField] float groundStickForce = 3f;        // keeps the capsule pressed onto slopes
    // Floor/wall threshold for WallClimbSensor. Not controller.slopeLimit: that's 90° while climbing.
    [SerializeField] float walkableSlopeLimit = 45f;
    [SerializeField] Vector3 velocity;
    [SerializeField] GameObject ViewmodelPlayer, ViewmodelSquid, SquidTrail, InkTankScaler;
    [SerializeField] GameObject InkExitSplashPrefab;
    [SerializeField] GameObject swimSplashParticlesPrefab;
    [SerializeField] float fallSplashMinHeight = 0.5f; // min fall to splash when landing in ink
    [SerializeField] Material mat;

    [Header("Ink")]
    [SerializeField] float inkRechargeRate = 0.1f;
    [SerializeField] float inkRechargeRateSquid = 0.55f;

    [Header("Climbing")]
    [SerializeField] float climbSlideCap = 3f;          // max gravity slide speed on a wall
    [SerializeField] float climbJumpScale = 0.4f;       // eject strength (jump while descending), x jump impulse
    [SerializeField] float wallJumpGraceDuration = 0.3f; // no re-grab for this long after ejecting
    [SerializeField] int missedWallGrace = 3;           // frames the wall can go undetected before dropping contact
    [SerializeField] float climbNormalSmoothing = 15f;  // visual easing toward a new wall normal
    [SerializeField] float climbEngageDot = 0.3f;       // how directly input must push into a wall to grab it

    [Header("Climb speed")]
    [SerializeField] float climbSpeedFactor = 0.8f;   // base climb speed, x own-ink swim speed
    [SerializeField] float climbBoostPerJump = 0.35f; // boost per jump (1 = full swim speed)
    [SerializeField] float climbBoostDecay = 0.7f;    // boost lost per second

    [Header("Popping off a wall")]
    [SerializeField] float climbPopForward = 2.5f;      // push over the lip when the wall/ink ends
    [SerializeField] float climbPopAirControl = 3f;     // air input speed until landing
    [SerializeField] float climbPopNoSwimTime = 0.2f;   // no re-grab for this long
    [SerializeField] float climbPopInkEdgeLift = 0.25f; // upward speed kept at the edge of our ink
    [SerializeField] float climbPopTopLift = 0.5f;      // upward speed kept at the top of a wall

    [Header("Network")]
    [SerializeField] PlayerMode playerMode = PlayerMode.Client;
    [SerializeField] UIController uiController;
    [SerializeField] float netLerpSpeed = 14f;

    [Header("Respawn")]
    [SerializeField] Vector3 spawnPoint = new Vector3(0, 3, 0); // fallback if no tagged spawn exists

    public bool IsLocalPlayer => playerMode == PlayerMode.Client;
    public float InkLevel => inkLevel;
    public bool IsSquid => swimMode;
    public bool IsDead => isDead;
    public float SlopeLimit => walkableSlopeLimit;
    // On walls only actual climbing counts (not mere sensor contact while falling past one).
    public bool IsInInk => swimMode && (OnOwnSurfaceInk || effectivelyClimbing);

    // Not while rising: the ink ray reaches below the capsule, so hopping just above ink would
    // count. (isGrounded alone flickers going uphill.) Remotes have no controller state.
    bool OnOwnSurfaceInk => surfaceTeam == team &&
        (controller == null || !controller.enabled || controller.isGrounded || velocityY <= 0f);
    bool InWallJumpGrace => Time.time < wallJumpGraceUntil;

    // Test harness / debug hooks. ScriptedInput replaces hardware input entirely.
    public ScriptedPlayerInput ScriptedInput { get; set; }
    public void PressJump() => Jump();
    public bool IsClimbing => effectivelyClimbing;
    public bool HasWallContact => isClimbing;
    public Vector3 ClimbNormal => climbNormal;
    public Vector3 ClimbIntent => climbIntent;
    public bool IsGrounded => controller != null && controller.enabled && controller.isGrounded;
    public bool ControllerEnabled => controller != null && controller.enabled;
    public Vector3 BodyCenter => transform.position + (controller != null ? controller.center : Vector3.zero);
    public Transform SquidVisual => ViewmodelSquid != null ? ViewmodelSquid.transform : null;
    public Transform CameraRig => playerCamera;
    public int SurfaceTeam => surfaceTeam;
    public float VerticalVelocity => velocityY;
    public float CurrentSpeed => realSpeed;
    public CollisionFlags LastCollisionFlags => controller != null ? controller.collisionFlags : CollisionFlags.None;

    CharacterController controller;
    ControlLayer input;
    int playerLayer, swimLayer;
    bool ownsMat; // mat is a runtime instance we must destroy
    Color teamColor;

    int surfaceTeam;
    bool swimMode, isDead;
    float inkLevel = 1f;
    float realSpeed, airSpeed, cameraPitch, velocityY;
    Vector2 currentDir, currentDirVelocity, currentMouseDelta, currentMouseDeltaVelocity, targetDir;
    Vector3 cameraBaseLocalPos, cameraFormOffset, cameraFormOffsetVelocity;

    SurfaceInkManager groundInk; // ink surface below, from GetInkTeam
    Vector2 groundInkUV;
    Vector3 groundNormal = Vector3.up;
    float lastGroundedY;
    bool wasGroundedPrev;
    bool prevInOwnInk;
    float lastExitSplatTime = -1f, lastEnterSplatTime = -1f;

    // Climbing. isClimbing = sensor sees an own-ink wall; effectivelyClimbing = actually on it.
    bool isClimbing, wasClimbing, effectivelyClimbing;
    int missedWallFrames;
    Vector3 climbNormal;       // raw, drives movement
    Vector3 visualClimbNormal; // eased, drives squid/trail orientation
    Vector3 climbIntent;       // world move direction, lets the sensor prefer the wall we move into
    SurfaceInkManager climbInk;
    Vector2 climbInkUV;
    float climbBoost;          // 0..1
    float wallJumpGraceUntil = -1f;
    float regrabBlockedUntil = -1f;
    bool lostWallAtInkEdge;    // why contact ended: our ink ran out vs the wall itself ended
    Vector3 lastClimbVelocity; // along the wall, excluding the pull into it
    Vector3 airMomentum;       // carried after a pop-off until landing or re-grabbing

    // Remote (Network mode) interpolation targets, fed by ApplyNetState.
    Vector3 netTargetPos;
    float netTargetYaw, netCamPitch;
    bool netInitialised;

    // Built on the main thread each frame, sent from OnNetTick (off-thread).
    ulong localSteamId;
    PlayerStateData cachedNetState;

    void Start()
    {
        controller = GetComponent<CharacterController>();
        playerLayer = LayerMask.NameToLayer("Player");
        swimLayer = LayerMask.NameToLayer("PlayerSwim");

        // NetGameManager sets playerMode right after Instantiate, before Start runs.
        if (playerMode == PlayerMode.Client)
        {
            input = new ControlLayer();
            input.Enable();
            input.Movement.Jump.performed += ctx => { if (ScriptedInput == null) Jump(); };
            input.Movement.Debug.performed += ctx => TestCheckScores();
            if (lockCursor){ Cursor.lockState = CursorLockMode.Locked; Cursor.visible = false; }
            cameraBaseLocalPos = playerCamera.localPosition;
            if (uiController == null) uiController = FindFirstObjectByType<UIController>();
            if (mat != null) { mat = InstanceMaterial(mat); ownsMat = true; } // fade our own copy, not the shared asset
            localSteamId = SteamGlobal.steamID.Value;
            SteamGlobal.OnNetTick += NetTickBroadcast;
        }
        else
        {
            Destroy(playerCamera.gameObject); // remote players don't own a camera
        }

        NetGameManager gm = NetGameManager.Instance;
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
        if (InkTankScaler != null)
        {
            Renderer r = InkTankScaler.GetComponentInChildren<Renderer>();
            if (r != null) r.material.color = teamColor;
        }
        if (SquidTrail != null)
        {
            Renderer tr = SquidTrail.GetComponentInChildren<Renderer>(true);
            if (tr != null) tr.material.color = teamColor;
            ParticleSystem ps = SquidTrail.GetComponentInChildren<ParticleSystem>(true);
            if (ps != null) { var m = ps.main; m.startColor = teamColor; }
        }
        if (ViewmodelSquid != null && ViewmodelSquid.transform.childCount > 0)
        {
            Renderer r = ViewmodelSquid.transform.GetChild(0).GetComponent<Renderer>();
            if (r != null) r.material.color = teamColor;
        }
        if (uiController != null) uiController.SetTeamColor(teamColor);
    }

    void Update()
    {
        if (playerMode == PlayerMode.Client)
        {
            UpdateMouseLook();
            UpdateMovement();
            UpdateCameraFormOffset();
            float distance = Vector3.Distance(Camera.main.transform.position, transform.position);
            mat.color = distance < 2
                ? new Color(1, 1, 1, Mathf.Clamp(distance / 2, 0, 1))
                : new Color(1, 1, 1, 1);
            cachedNetState = GetNetState(localSteamId, 0); // tick is stamped in NetTickBroadcast
        }
        else if (playerMode == PlayerMode.Network)
        {
            UpdateNetInterpolation();
        }

        GetInkTeam();
        UpdateInk();
        UpdateViewmodels();
        UpdateSquidTrail();
    }

    void UpdateNetInterpolation()
    {
        if (!netInitialised) return;
        transform.position = Vector3.Lerp(transform.position, netTargetPos, Time.deltaTime * netLerpSpeed);
        Quaternion targetRot = Quaternion.Euler(0f, netTargetYaw, 0f);
        transform.rotation = Quaternion.Slerp(transform.rotation, targetRot, Time.deltaTime * netLerpSpeed);
        if (playerCamera != null) playerCamera.localEulerAngles = Vector3.right * netCamPitch;
    }

    // Eases out the offset that cancels form-switch nudges, so the camera glides instead of snapping.
    void UpdateCameraFormOffset()
    {
        if (playerCamera == null) return;
        cameraFormOffset = Vector3.SmoothDamp(cameraFormOffset, Vector3.zero, ref cameraFormOffsetVelocity, cameraFormSmoothTime);
        playerCamera.localPosition = cameraBaseLocalPos + cameraFormOffset;
    }

    void UpdateMouseLook()
    {
        Vector2 targetMouseDelta = ScriptedInput != null ? ScriptedInput.look : input.Movement.Look.ReadValue<Vector2>();
        currentMouseDelta = Vector2.SmoothDamp(currentMouseDelta, targetMouseDelta, ref currentMouseDeltaVelocity, mouseSmoothTime);
        cameraPitch -= currentMouseDelta.y * mouseSensitivity;
        cameraPitch = Mathf.Clamp(cameraPitch, -70.0f, 80.0f);
        playerCamera.localEulerAngles = Vector3.right * cameraPitch;
        transform.Rotate(Vector3.up * currentMouseDelta.x * mouseSensitivity);
    }

    void UpdateMovement()
    {
        if (isDead) return;

        float prevHeight = controller.height;
        float prevRadius = controller.radius;
        bool wasClimbingWall = wasClimbing;
        bool swimHeld = ScriptedInput != null ? ScriptedInput.swim : input.Movement.Squidmode.ReadValue<float>() != 0;
        if (swimHeld)
        {
            swimMode = true;
            // Must drop before shrinking: Unity disables the controller if stepOffset > height + radius*2.
            controller.stepOffset = 0f;
            controller.height = 0.1f;
            controller.radius = 0.1f;
        }
        else
        {
            swimMode = false;
            isClimbing = false;
            controller.height = 1.92f;
            controller.radius = 0.5f;
        }
        if (controller.height != prevHeight)
        {
            // The capsule resizes around a fixed centre, so move the origin to keep its bottom on the floor.
            Vector3 beforeNudge = transform.position;
            transform.position += Vector3.up * ((controller.height - prevHeight) / 2f);

            // Growing against a wall (leaving swim mid-climb) would overlap it.
            if (wasClimbingWall && controller.radius > prevRadius)
                transform.position += climbNormal * (controller.radius - prevRadius + controller.skinWidth);

            Physics.SyncTransforms(); // Auto Sync Transforms is off; Move() would otherwise undo the nudge
            cameraFormOffset -= transform.InverseTransformVector(transform.position - beforeNudge); // eased out by UpdateCameraFormOffset
        }
        bool enteredSwimModeThisFrame = swimMode && controller.height != prevHeight;

        bool grounded = controller.isGrounded;
        bool justLanded = grounded && !wasGroundedPrev;
        float fallHeight = justLanded ? lastGroundedY - transform.position.y : 0f;
        if (grounded) lastGroundedY = transform.position.y;
        wasGroundedPrev = grounded;

        gameObject.layer = swimMode ? swimLayer : playerLayer;
        if (surfaceTeam != 0)
        {
            bool inOwnInk = surfaceTeam == team;
            if (swimMode) realSpeed = inOwnInk ? swimSpeed + 2 : swimSpeed / 2;
            else          realSpeed = inOwnInk ? moveSpeed : moveSpeed - 2;
        }
        else
        {
            realSpeed = swimMode ? swimSpeed / 4 : moveSpeed;
        }
        targetDir = ScriptedInput != null ? ScriptedInput.move : input.Movement.Move.ReadValue<Vector2>();
        targetDir.Normalize();
        currentDir = Vector2.SmoothDamp(currentDir, targetDir, ref currentDirVelocity, moveSmoothTime);

        // Grabbing a wall needs input pushing into it; once on, only reaching the floor without
        // pressing up lets go (or the sensor losing it).
        bool wantsToClimb = currentDir.y > 0.01f;
        bool atWallBase = controller.isGrounded && !wantsToClimb;
        Vector3 flatMove = transform.forward * currentDir.y + transform.right * currentDir.x;
        bool pushingIntoWall = isClimbing && Vector3.Dot(flatMove, -climbNormal) > climbEngageDot;
        bool regrabBlocked = Time.time < regrabBlockedUntil;
        bool canEngageClimb = !InWallJumpGrace && (wasClimbing ? !atWallBase : pushingIntoWall && !regrabBlocked);
        bool climbing = swimMode && isClimbing && canEngageClimb;
        effectivelyClimbing = climbing;
        if (climbing)
        {
            if (!wasClimbing) { velocityY = 0f; airMomentum = Vector3.zero; }
            wasClimbing = true;

            // Nothing below on a wall, so speed comes from own-ink swim speed, boosted by jumps.
            climbBoost = Mathf.Max(0f, climbBoost - climbBoostDecay * Time.deltaTime);
            realSpeed = (swimSpeed + 2) * Mathf.Lerp(climbSpeedFactor, 1f, climbBoost);

            controller.slopeLimit = 90f;
            controller.stepOffset = 0f;

            velocityY += gravity * 0.1f * Time.deltaTime;
            velocityY = Mathf.Max(velocityY, -climbSlideCap); // below realSpeed so gravity slows a climb, never stops it

            Vector3 wallRight = Vector3.Cross(Vector3.up, climbNormal).normalized;
            Vector3 wallUp    = Vector3.Cross(climbNormal, wallRight).normalized;
            climbIntent = wallUp * currentDir.y - wallRight * currentDir.x;
            lastClimbVelocity = wallUp    * (currentDir.y * realSpeed + velocityY)
                              - wallRight *  currentDir.x * realSpeed;
            velocity = lastClimbVelocity - climbNormal * 2f;
            controller.Move(velocity * Time.deltaTime);
        }
        else
        {
            if (wasClimbing)
            {
                // Wall (or our ink on it) ran out, rather than leaving on purpose: keep momentum.
                bool lostWall = swimMode && !isClimbing && !InWallJumpGrace;
                if (lostWall) PopOffWall();
                else velocityY = Mathf.Max(velocityY, 0f);
            }
            wasClimbing = false;

            controller.slopeLimit = walkableSlopeLimit;
            controller.stepOffset = swimMode ? 0f : 0.3f; // no stepping in squid form, it bounces off walls
            velocityY += (gravity * 3) * Time.deltaTime;
            if (controller.isGrounded)
            {
                airSpeed = realSpeed;
                airMomentum = Vector3.zero;
                if (velocityY <= 0f) velocityY = -groundStickForce; // guarded so it can't cancel a jump this frame
            }

            if (velocityY > 10) velocityY = 10;
            float hSpeed = controller.isGrounded ? realSpeed : airSpeed;
            climbIntent = flatMove;
            Vector3 move = flatMove * hSpeed;
            // Follow walkable slopes; a horizontal move launches off downhill slopes every frame.
            if (controller.isGrounded && velocityY <= 0f && groundNormal.y >= Mathf.Cos(walkableSlopeLimit * Mathf.Deg2Rad))
                move = Vector3.ProjectOnPlane(move, groundNormal).normalized * move.magnitude;
            velocity = move + airMomentum + Vector3.up * velocityY;
            controller.Move(velocity * Time.deltaTime);
        }

        bool onWallInk = effectivelyClimbing;
        bool inOwnInkNow = swimMode && team != 0 && (OnOwnSurfaceInk || onWallInk);
        Vector3 inkNormal = onWallInk ? climbNormal : groundNormal;

        // Enter splash only when entering swim form on ink or landing in ink from a height.
        bool enteredSwimModeOnInk  = enteredSwimModeThisFrame && surfaceTeam == team;
        bool landedOnInkFromHeight = justLanded && surfaceTeam == team && fallHeight > fallSplashMinHeight;
        if ((enteredSwimModeOnInk || landedOnInkFromHeight) && Time.time - lastEnterSplatTime >= 0.2f)
        {
            lastEnterSplatTime = Time.time;
            InkParticles.Spawn(swimSplashParticlesPrefab, transform.position + controller.center,
                Quaternion.FromToRotation(Vector3.up, groundNormal), teamColor);
        }

        // Exit splash on leaving own ink while still swimming.
        if (prevInOwnInk && !inOwnInkNow && swimMode && InkExitSplashPrefab != null && Time.time - lastExitSplatTime >= 0.2f)
        {
            lastExitSplatTime = Time.time;
            GameObject splash = Instantiate(InkExitSplashPrefab, transform.position + controller.center, Quaternion.FromToRotation(Vector3.up, inkNormal));
            splash.GetComponent<InkEmitter>().Setup(team, 3, true);
        }
        prevInOwnInk = inOwnInkNow;

        if (transform.position.y < -10) Respawn();
    }

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

    IEnumerator RespawnAfterDelay(float delay)
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

    // Instantly moves the local player (feet at feetPos) and resets all motion and climb state.
    public void PlaceAt(Vector3 feetPos, float yaw)
    {
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
            controller.enabled = true; // never leave it disabled
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
        cameraFormOffset = cameraFormOffsetVelocity = Vector3.zero; // a teleport snaps the camera
    }

    // Remote copies: snap on Teleport instead of interpolating across the map.
    public void ApplyTeleport(Vector3 pos, float yaw)
    {
        if (playerMode != PlayerMode.Network) return;
        netTargetPos = pos;
        netTargetYaw = yaw;
        transform.position = pos;
        transform.rotation = Quaternion.Euler(0f, yaw, 0f);
    }

    // ── Networking API (driven by NetGameManager) ─────────────────────────
    public void SetPlayerMode(PlayerMode mode) => playerMode = mode;
    public void SetTeam(int t) => team = t;

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

        if (s.dead != isDead)
        {
            isDead = s.dead;
            if (isDead) HideModels(); else ShowModels();
        }

        if (controller != null)
        {
            controller.height = swimMode ? 0.1f : 1.92f;
            controller.radius = swimMode ? 0.1f : 0.5f;
        }

        if (!netInitialised)
        {
            // Remotes are driven by interpolation only; snap to the first pose.
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
        ViewmodelPlayer.SetActive(ViewmodelPlayer.transform.localScale.y > 0.05f);

        if (swimMode && currentDir.magnitude > 0.05f)
        {
            GetSwimPose(out Vector3 moveDir, out Vector3 upHint);
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

        GetSwimPose(out Vector3 moveDir, out Vector3 upHint);
        // Indent the ink's normal map along the swim path.
        if (effectivelyClimbing) { if (climbInk != null) climbInk.PaintTrailNormal(climbInkUV, 4, 4, 0.01f); }
        else if (groundInk != null) groundInk.PaintTrailNormal(groundInkUV, 4, 4, 0.01f);

        if (moveDir.sqrMagnitude > 0.001f)
            SquidTrail.transform.rotation = Quaternion.LookRotation(moveDir, upHint);
        SquidTrail.transform.localScale = Vector3.one * moveMag;
    }

    // Movement direction and up vector for the squid and trail, flat on the wall or floor/slope.
    void GetSwimPose(out Vector3 moveDir, out Vector3 upHint)
    {
        if (effectivelyClimbing)
        {
            Vector3 wallRight = Vector3.Cross(Vector3.up, visualClimbNormal).normalized;
            Vector3 wallUp    = Vector3.Cross(visualClimbNormal, wallRight).normalized;
            moveDir = (wallUp * currentDir.y - wallRight * currentDir.x).normalized;
            upHint  = visualClimbNormal;
        }
        else
        {
            Vector3 flatDir = (transform.forward * currentDir.y + transform.right * currentDir.x).normalized;
            moveDir = Vector3.ProjectOnPlane(flatDir, groundNormal).normalized; // keep orthogonal to upHint
            upHint  = groundNormal;
        }
    }

    void UpdateInk()
    {
        if (playerMode != PlayerMode.Client) return;
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

    // Called every frame by WallClimbSensor while it sees an own-ink wall.
    public void SetClimbContact(Vector3 normal, SurfaceInkManager wallInk, Vector2 wallUV)
    {
        bool wasAlreadyClimbing = isClimbing;
        isClimbing = true;
        missedWallFrames = 0;
        climbNormal = normal; // raw, so movement can turn sharp corners at full speed
        visualClimbNormal = wasAlreadyClimbing
            ? Vector3.Slerp(visualClimbNormal, normal, Time.deltaTime * climbNormalSmoothing)
            : normal;
        climbInk = wallInk;
        climbInkUV = wallUV;
    }

    // Leave the wall keeping its momentum: scaled upward speed becomes a hop, plus a push over
    // the lip, limited air control until landing, and a brief no-regrab window.
    void PopOffWall()
    {
        float lift = lostWallAtInkEdge ? climbPopInkEdgeLift : climbPopTopLift;
        velocityY = Mathf.Max(lastClimbVelocity.y, 0f) * lift;
        airMomentum = Vector3.ProjectOnPlane(lastClimbVelocity, Vector3.up) - climbNormal * climbPopForward;
        airSpeed = climbPopAirControl;
        regrabBlockedUntil = Time.time + climbPopNoSwimTime;
    }

    // Brief misses (rounding a corner between rays) get a few frames' grace. Reaching the edge
    // of our ink or the top of the wall is definite and drops contact immediately.
    public void ClearClimbContact(bool atInkEdge = false, bool atWallTop = false)
    {
        missedWallFrames++;
        if (atInkEdge || atWallTop || missedWallFrames > missedWallGrace)
        {
            if (isClimbing) lostWallAtInkEdge = atInkEdge;
            isClimbing = false;
            climbInk = null;
        }
    }

    void OnDestroy()
    {
        if (playerMode == PlayerMode.Client)
            SteamGlobal.OnNetTick -= NetTickBroadcast;
        if (ownsMat && mat != null) Destroy(mat);
        input?.Dispose(); // otherwise the Jump callback outlives us
    }

    // Off the main thread: only touches the cached snapshot.
    void NetTickBroadcast(uint tick)
    {
        PlayerStateData snap = cachedNetState;
        if (snap == null) return;
        snap.tick = tick;
        // Unreliable: a newer snapshot follows every tick, and reliable delivery stalled on loss.
        SteamGlobal.SendAllData((ushort)NetMsg.PlayerState, snap, reliable: false);
    }

    void Jump()
    {
        if (effectivelyClimbing)
        {
            float wallClimbSpeed = currentDir.y * realSpeed + velocityY;
            if (wallClimbSpeed < 0f)
            {
                // Descending: eject from the wall.
                velocityY += jump * 2 * climbJumpScale;
                wallJumpGraceUntil = Time.time + wallJumpGraceDuration;
            }
            else
            {
                // Climbing or holding: boost climb speed (decays, so spam to keep it) and cancel the slide.
                climbBoost = Mathf.Min(1f, climbBoost + climbBoostPerJump);
                velocityY = Mathf.Max(velocityY, 0f);
            }
            return;
        }
        if (controller.isGrounded || IsInInk) { velocityY += jump * 2; }
    }

    void TestCheckScores()
    {
        NetGameManager gm = NetGameManager.Instance;
        gm.GetScores();
        gm.GetTopDownScores();
    }

    void GetInkTeam()
    {
        Vector3 origin = transform.position + controller.center;
        float reach = (controller.height / 2) + 0.3f;
        if (Physics.Raycast(origin, Vector3.down, out RaycastHit inkHit, reach, PhysicsLayers.Environment, QueryTriggerInteraction.Ignore))
        {
            Debug.DrawLine(origin, inkHit.point, Color.blue);
            groundInk = inkHit.collider.GetComponent<SurfaceInkManager>();
            groundInkUV = inkHit.textureCoord;
            groundNormal = inkHit.normal;
            surfaceTeam = groundInk != null ? groundInk.getSurfaceTeam(groundInkUV) : 0;
        }
        else if (controller.enabled && controller.isGrounded)
        {
            // On a ledge lip the centre ray misses; keep the last reading so speed isn't lost.
            Debug.DrawRay(origin, Vector3.down * reach, Color.yellow);
        }
        else
        {
            Debug.DrawRay(origin, Vector3.down * reach, Color.red);
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

// Input fed to a player in place of hardware (PlayerController.ScriptedInput).
public class ScriptedPlayerInput
{
    public Vector2 move; // x = strafe, y = forward
    public Vector2 look; // per-frame mouse delta
    public bool swim;
}

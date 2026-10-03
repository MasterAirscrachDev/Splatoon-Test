using System.Collections;
using UnityEngine;

public partial class PlayerController : MonoBehaviour, ISuperJumpTarget
{
    [SerializeField] Transform playerCamera = null;
    [SerializeField] int team = 0;
    public int Team => team;
    [Header("Look")]
    [SerializeField] float mouseSensitivity = 3.5f;  // degrees per mouse count
    [SerializeField][Range(0.0f, 0.5f)] float mouseSmoothTime = 0.03f;
    [SerializeField] Vector2 stickSpeed = new Vector2(180f, 120f); // right stick at full tilt: degrees per second (turn, up/down)
    [SerializeField] float stickCurve = 1.8f;        // response: gentle near the middle for fine aim, full speed at the edge
    [SerializeField] float stickBoost = 1.6f;        // turning speeds up to this many times faster while held at the edge...
    [SerializeField] float stickBoostTime = 0.35f;   // ...over this long
    [SerializeField] bool invertY;
    [SerializeField] float gyroSensitivity = 1.5f;  // camera degrees per degree the controller turns (Steam Input gyro)
    [SerializeField] bool gyroInvertY;
    const float RecenterSpeed = 600f;                // degrees per second back to level
    const float StickEdge = 0.9f;                    // tilt that counts as "at the edge" for the boost

    [Header("Movement Settings")]
    [SerializeField] float gravity = -13.0f;
    [SerializeField] float jump = 8.0f;
    [SerializeField][Range(0.0f, 0.5f)] float moveSmoothTime = 0.3f;
    [SerializeField] float moveSpeed = 6.0f;
    [SerializeField] float swimSpeed = 10.0f;
    [SerializeField] bool lockCursor = true;
    [SerializeField] Vector3 velocity;
    [SerializeField] GameObject ViewmodelPlayer, ViewmodelSquid, InkTankScaler;
    [SerializeField] GameObject InkExitSplashPrefab;
    [SerializeField] GameObject swimSplashParticlesPrefab;
    [SerializeField] Material mat;

    [Header("Ink")]
    [SerializeField] float inkRechargeRate = 0.1f;
    [SerializeField] float inkRechargeRateSquid = 0.55f;

    [Header("Super Jump")]
    [SerializeField] float superJumpChargeTime = 2f;
    [SerializeField] float superJumpHeight = 15f;              // arc peak above the straight line...
    [SerializeField] float superJumpFlightTime = 1.2f;

    [Header("Network")]
    [SerializeField] PlayerMode playerMode = PlayerMode.Client;
    [SerializeField] UIController uiController;

    // Splatted: the camera circles over where it happened (or the last ground before a fall out of
    // the world) while the popup says who did it, then we're back at spawn, held in swim form for a
    // moment before control returns.
    const float SplattedTime = 4f;      // spinning over the spot
    const float RespawnHoldTime = 1f;   // at spawn, in swim form, before we can act
    const float DeathCamSpin = 50f;     // degrees per second around the spot
    const float DeathCamPitch = 35f;    // looking down on it (the camera sits 7.5m back along the rig)
    const float DeathCamHeight = 1f;    // the rig's pivot above the spot
    public const float KillHeight = -10f;            // below this we've fallen out of the world
    public const string FellCause = "Fell out of bounds";

    [Header("Respawn")]
    [SerializeField] Vector3 spawnPoint = new Vector3(0, 3, 0); // fallback if no tagged spawn exists

    // Floor/wall threshold, shared with WallClimbSensor. Not controller.slopeLimit: that's 90° while climbing.
    const float WalkableSlopeLimit = 45f;
    const float FallSplashMinHeight = 0.5f;  // min fall to splash when landing in ink
    const float ViewmodelSwitchSpeed = 12f;  // kid/squid model scale easing
    // Fastest we can rise: a jump's push (2 x jump) is clipped to this before gravity, so it peaks at
    // about 1.52m at any frame rate (the height it had at 75 fps before it was made exact).
    const float MaxRiseSpeed = 10.47f;
    const float AimHold = 0.3f;              // the body keeps facing the view this long after the last aimed action
    const float ModelTurnRate = 12f;         // the body turning to the way we move
    const float NetLerpSpeed = 14f;          // remote copies easing toward their latest state

    public bool IsLocalPlayer => playerMode == PlayerMode.Client;
    public float InkLevel => inkLevel;
    public bool IsSquid => swimMode;
    public bool IsDead => isDead;
    public bool KidFormReady => !swimMode && (ViewmodelPlayer == null || ViewmodelPlayer.transform.localScale.y >= 0.98f); // out of swim form and fully grown: can use ink
    public Color TeamColour => teamColor;
    public bool IsRespawning => Time.time < respawnHoldUntil; // back at spawn, not yet released
    public float RespawnIn => isDead ? Mathf.Max(0f, respawnAt - Time.time) : 0f;
    public Vector3 DeathSpot => deathSpot;        // what the death camera circles
    public bool DeathCamera => deathCam;
    public ulong KilledBy { get; private set; }   // 0: nobody (fell)
    public string KilledWith { get; private set; }

    // A client-side player was splatted: by whom (0 for nobody) and with what.
    public static event System.Action<PlayerController, ulong, string> Splatted;
    public float SlopeLimit => WalkableSlopeLimit;
    // On walls only actual climbing counts (not mere sensor contact while falling past one).
    public bool IsInInk => swimMode && (OnOwnSurfaceInk || effectivelyClimbing);

    // Not while rising: the ink ray reaches below the capsule, so hopping just above ink would
    // count. (isGrounded alone flickers going uphill.) Remotes have no controller state.
    bool OnOwnSurfaceInk => surfaceTeam == team && superJumpPhase != SuperJumpPhase.Flying &&
        (controller == null || !controller.enabled || controller.isGrounded || velocityY <= 0f);
    bool InWallJumpGrace => Time.time < wallJumpGraceUntil;

    // Extra locally simulated player for tests (NetGameManager.SpawnTestPlayer); set before Start.
    public bool IsTestPlayer { get; set; }

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
    public SuperJumpPhase SuperJumpState => superJumpPhase;
    public float SuperJumpChargeTime => superJumpChargeTime;
    public Vector3 SuperJumpLanding => superJumpLanding;
    public bool IsSuperJumping => superJumpPhase != SuperJumpPhase.None;
    public Vector3 TravelVelocity => travelVelocity;

    CharacterController controller;
    ControlLayer input;
    int playerLayer, swimLayer;
    bool ownsMat; // mat is a runtime instance we must destroy
    Color teamColor;

    int surfaceTeam;
    bool swimMode, isDead, deathCam, recentering;
    float stickEdgeTime;
    Vector3 deathSpot, lastGroundPos;
    float deathCamYaw, respawnAt, respawnHoldUntil = -1f;
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
    float missedWallTime;      // how long the wall has gone undetected
    const float WallGrace = 0.05f; // brief misses (rounding a corner between rays) don't drop contact
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

    // Super Jump: charge in place, then a scripted arc from superJumpFrom to superJumpTo.
    SuperJumpPhase superJumpPhase;
    ISuperJumpTarget superJumpTarget;
    Vector3 superJumpFrom, superJumpTo;
    Vector3 superJumpLanding; // locked when the jump starts: ground under the target, or the target itself
    bool superJumpLandsOnGround;
    float superJumpTimer, superJumpDuration, superJumpArc;

    Vector3 lastFramePos, travelVelocity; // actual motion, for the squid model's facing

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
        controller.minMoveDistance = 0f; // every move counts: tiny ones (high frame rates, slow motion) skipped would drop isGrounded
        modelYaw = netModelYaw = transform.eulerAngles.y;
        playerLayer = LayerMask.NameToLayer("Player");
        swimLayer = LayerMask.NameToLayer("PlayerSwim");

        // NetGameManager sets playerMode right after Instantiate, before Start runs.
        if (playerMode == PlayerMode.Client)
        {
            input = new ControlLayer();
            input.Enable();
            input.Movement.Jump.performed += ctx => { if (ScriptedInput == null && !InputGate.Blocked) Jump(); };
            input.Movement.Debug.performed += ctx => TestCheckScores();
            cameraBaseLocalPos = playerCamera.localPosition;
            if (mat != null) { mat = InstanceMaterial(mat); ownsMat = true; } // fade our own copy, not the shared asset
            if (IsTestPlayer)
            {
                // Driven by tests alongside the real local player: no view, cursor, HUD or network of its own.
                foreach (Camera cam in playerCamera.GetComponentsInChildren<Camera>(true)) cam.enabled = false;
                foreach (AudioListener al in playerCamera.GetComponentsInChildren<AudioListener>(true)) al.enabled = false;
            }
            else
            {
                if (lockCursor) { Cursor.lockState = CursorLockMode.Locked; Cursor.visible = false; }
                if (uiController == null) uiController = FindFirstObjectByType<UIController>();
                localSteamId = SteamGlobal.steamID.Value;
                SteamGlobal.OnNetTick += NetTickBroadcast;
            }
        }
        else
        {
            Destroy(playerCamera.gameObject); // remote players don't own a camera
        }

        ApplyTeamColor();
        NetGameManager.TeamColoursChanged += ApplyTeamColor;
    }

    void OnEnable() => input?.Enable();

    void OnDisable()
    {
        input?.Disable();
        cachedNetState = null; // benched: stop broadcasting state
    }

    void Update()
    {
        if (playerMode == PlayerMode.Client)
        {
            if (deathCam) UpdateDeathCamera(); else UpdateLook();
            UpdateMovement();
            if (!deathCam) UpdateCameraFormOffset();
            Camera view = Camera.main;
            float distance = view != null ? Vector3.Distance(view.transform.position, transform.position) : float.MaxValue;
            mat.color = distance < 2
                ? new Color(1, 1, 1, Mathf.Clamp(distance / 2, 0, 1))
                : new Color(1, 1, 1, 1);
            cachedNetState = GetNetState(localSteamId, 0); // tick is stamped in NetTickBroadcast
        }
        else if (playerMode == PlayerMode.Network)
        {
            UpdateNetInterpolation();
        }

        if (Time.deltaTime > 0f) travelVelocity = (transform.position - lastFramePos) / Time.deltaTime;
        lastFramePos = transform.position;

        GetInkTeam();
        UpdateInk();
        UpdateViewmodels();
        UpdateSquidTrail();
    }

    public float CameraPitch => cameraPitch; // tests
    public Vector2 StickSpeed => stickSpeed;
    public float GyroSensitivity => gyroSensitivity;

    // As a target: teammates can jump to us while we're alive and on their team.
    public Vector3 JumpPosition => transform.position;

    Coroutine pendingRespawn;

    // The team's first tagged spawn (by name, so the map shows a stable one), or the fallback.
    public Vector3 SpawnPosition
    {
        get
        {
            GameObject[] pts = GameObject.FindGameObjectsWithTag(team == 1 ? "AlphaSpawn" : "BetaSpawn");
            if (pts.Length == 0) return spawnPoint;
            System.Array.Sort(pts, (a, b) => string.CompareOrdinal(a.name, b.name));
            return pts[0].transform.position;
        }
    }
    public PlayerRole Role { get; private set; } = PlayerRole.Alpha;
    public string DisplayName { get; private set; } = "";
    public ulong OwnerId { get; private set; } // Steam id of the client that owns this player
    public PlayerHitbox Hitbox => hitbox != null ? hitbox : (hitbox = GetComponentInChildren<PlayerHitbox>(true));
    PlayerHitbox hitbox;

    // Swimming through ink: SwimWake lays the wake, and the ink's normal map is indented along the path.
    public bool Wading => !isDead && swimMode && IsInInk && currentDir.magnitude > 0.05f;

    // The surface's normal under the squid (the floor's, or the wall's while climbing).
    public Vector3 SwimUp { get { GetSwimPose(out _, out Vector3 up); return up; } }

    void UpdateInk()
    {
        if (playerMode != PlayerMode.Client) return;
        float rate = team != 0 && IsInInk ? inkRechargeRateSquid : inkRechargeRate;
        inkLevel = Mathf.Clamp01(inkLevel + rate * Time.deltaTime);
        ShowInkLevel();
        if (uiController != null)
        {
            uiController.SetSwimMode(swimMode);
            uiController.SetInkLevel(inkLevel);
        }
    }

    public void RefillInk() => inkLevel = 1f;

    // Set by PlayerLoadout locally, from state updates on remote copies.
    public bool Shielded { get; set; }       // bubble shield running
    public bool SpecialCharged { get; set; } // special ready to use
    public bool SubReady { get; set; }       // enough ink for the sub (the tank's light)
    public int WeaponIndex { get; set; }     // equipped main weapon (PlayerLoadout)
    public bool WeaponDown { get; set; }     // weapon in its down pose (a roller rolling), synced so remotes show it
    public float WeaponSpeedMultiplier { get; set; } = 1f; // x walking speed, set by the weapon (rolling)
    public bool AttackHeld { get; set; }     // the attack button is down (set by the weapon): out of swim form, it acts at once
    public bool ActionQueued { get; set; }   // a sub waiting for the kid form (set by PlayerLoadout): likewise

    // Facing: the kid model turns to the way we move (as the squid does), except while aiming (a
    // weapon firing, a sub going out), when it faces the view at once. Synced as modelYaw.
    float aimUntil, modelYaw, netModelYaw;
    bool netAiming;
    public bool Aiming => playerMode == PlayerMode.Network ? netAiming : Time.time < aimUntil;
    public float AimPitch => playerMode == PlayerMode.Network ? netCamPitch : cameraPitch; // degrees, down positive
    public float ModelYaw => modelYaw; // world yaw of the kid model

    public bool ConsumeInk(float amount)
    {
        if (inkLevel < amount || !KidFormReady) return false;
        inkLevel -= amount;
        return true;
    }

    void OnDestroy()
    {
        if (playerMode == PlayerMode.Client)
            SteamGlobal.OnNetTick -= NetTickBroadcast;
        if (ownsMat && mat != null) Destroy(mat);
        input?.Disable();
        input?.Dispose(); // otherwise the Jump callback outlives us
        NetGameManager.TeamColoursChanged -= ApplyTeamColor;
    }

    void TestCheckScores()
    {
        NetGameManager gm = NetGameManager.Instance;
        gm.GetScores();
        gm.GetTopDownScores();
    }
}

public enum PlayerMode
{
    Client, Network
}

public enum SuperJumpPhase
{
    None, Charging, Flying
}

// Input fed to a player in place of hardware (PlayerController.ScriptedInput).
public class ScriptedPlayerInput
{
    public Vector2 move;      // x = strafe, y = forward
    public Vector2 look;      // per-frame mouse delta
    public Vector2 lookStick; // right stick tilt
    public bool recenter;     // the Recenter button, while true
    public bool swim;
}

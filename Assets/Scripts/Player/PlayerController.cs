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
    [Header("Ink")]
    [SerializeField] float inkRechargeRate = 0.1f;
    [SerializeField] float inkRechargeRateSquid = 0.55f;
    [Header("Network")]
    [SerializeField] PlayerMode playerMode = PlayerMode.Client;
    [SerializeField] UIController uiController;
    public bool IsLocalPlayer => playerMode == PlayerMode.Client;
    int surfaceTeam = 0;
    bool swimMode;
    float inkLevel = 1f;
    public float InkLevel => inkLevel;
    public bool IsSquid => swimMode;
    public bool IsInInk => swimMode && (surfaceTeam == team || (isClimbing && climbSurfaceTeam == team));
    float realSpeed, airSpeed, cameraPitch = 0.0f, velocityY = 0.0f;
    CharacterController controller = null;
    ControlLayer input;
    [SerializeField] Material mat;
    bool grounded;
    bool isClimbing;
    bool wasClimbing;
    Vector3 climbNormal;
    int climbSurfaceTeam;
    bool prevInOwnInk;
    float lastExitSplatTime = -1f;
    bool effectivelyClimbing;
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
            input.Movement.Jump.performed += ctx => Jump();
            input.Movement.Debug.performed += ctx => TestCheckScores();
            if (lockCursor){ Cursor.lockState = CursorLockMode.Locked; Cursor.visible = false; }
            if (uiController == null) uiController = FindFirstObjectByType<UIController>();
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
        Vector2 targetMouseDelta = input.Movement.Look.ReadValue<Vector2>();
        currentMouseDelta = Vector2.SmoothDamp(currentMouseDelta, targetMouseDelta, ref currentMouseDeltaVelocity, mouseSmoothTime);
        cameraPitch -= currentMouseDelta.y * mouseSensitivity;
        cameraPitch = Mathf.Clamp(cameraPitch, -70.0f, 80.0f);
        playerCamera.localEulerAngles = Vector3.right * cameraPitch;
        transform.Rotate(Vector3.up * currentMouseDelta.x * mouseSensitivity);
    }
    void UpdateMovement()
    {
        if (playerMode != PlayerMode.Client) return;
        if(input.Movement.Squidmode.ReadValue<float>() != 0){
            swimMode = true;
            controller.height = 0.1f;
            controller.radius = 0.1f;
        }
        else{
            swimMode = false;
            isClimbing = false; // dismount wall when leaving squid mode
            controller.height = 1.92f;
            controller.radius = 0.5f;
        }
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
        targetDir = input.Movement.Move.ReadValue<Vector2>(); targetDir.Normalize();
        currentDir = Vector2.SmoothDamp(currentDir, targetDir, ref currentDirVelocity, moveSmoothTime);

        // Eject from climbing when we've descended to the floor at the base of a wall.
        // The sensor keeps reporting contact while the trigger overlaps the wall, so we
        // gate on "grounded and not actively climbing up" rather than on sensor state.
        // Holding up (currentDir.y > 0) still lets the player mount a wall from the ground.
        bool atWallBase = controller.isGrounded && currentDir.y <= 0.01f;
        bool climbing = swimMode && isClimbing && climbSurfaceTeam == team && !atWallBase;
        effectivelyClimbing = climbing;
        if (climbing)
        {
            // Reset any accumulated falling velocity on the first frame of wall contact.
            if (!wasClimbing) velocityY = 0f;
            wasClimbing = true;

            controller.slopeLimit = 90f;
            controller.stepOffset = 0f;

            velocityY += gravity * 0.1f * Time.deltaTime;
            velocityY  = Mathf.Max(velocityY, -realSpeed);

            Vector3 wallRight = Vector3.Cross(Vector3.up, climbNormal).normalized;
            Vector3 wallUp    = Vector3.Cross(climbNormal, wallRight).normalized;
            velocity = wallUp    * (currentDir.y * realSpeed + velocityY)
                     - wallRight *  currentDir.x * realSpeed
                     - climbNormal * 2f;
            controller.Move(velocity * Time.deltaTime);
            return;
        }

        // Leaving wall: discard any negative wall-slide velocity so it doesn't carry into freefall.
        if (wasClimbing) velocityY = Mathf.Max(velocityY, 0f);
        wasClimbing = false;

        controller.slopeLimit = 45f;
        controller.stepOffset = swimMode ? 0f : 0.3f; // disable step logic in squid mode to avoid "bouncing" off walls
        velocityY += (gravity * 3) * Time.deltaTime;
        if (controller.isGrounded){ velocityY = 0.0f; airSpeed = realSpeed; }

        if (velocityY > 10){ velocityY = 10; }
        float hSpeed = controller.isGrounded ? realSpeed : airSpeed;
        velocity = (transform.forward * currentDir.y + transform.right * currentDir.x) * hSpeed + Vector3.up * velocityY;
        controller.Move(velocity * Time.deltaTime);

        // Spawn the ink-exit splash when transitioning out of own ink while swimming.
        // The && swimMode guard prevents triggering when the player leaves swim mode while still on ink.
        bool inOwnInkNow = swimMode && team != 0 && (surfaceTeam == team || (isClimbing && climbSurfaceTeam == team));
        if (prevInOwnInk && !inOwnInkNow && swimMode && InkExitSplashPrefab != null && Time.time - lastExitSplatTime >= 0.2f)
        {
            lastExitSplatTime = Time.time;
            Vector3 exitNormal = (isClimbing && climbSurfaceTeam == team) ? climbNormal : Vector3.up;
            GameObject splash = Instantiate(InkExitSplashPrefab, transform.position + controller.center, Quaternion.FromToRotation(Vector3.up, exitNormal));
            splash.GetComponent<InkEmitter>().Setup(team, 3, true);
        }
        prevInOwnInk = inOwnInkNow;

        if(transform.position.y < -10){
            Respawn();
        }
    }

    [Header("Respawn")]
    [SerializeField] Vector3 spawnPoint = new Vector3(0, 3, 0); // fallback if no tagged spawn exists
    public void Respawn()
    {
        controller.enabled = false;
        string tag = team == 1 ? "AlphaSpawn" : "BetaSpawn";
        GameObject[] pts = GameObject.FindGameObjectsWithTag(tag);
        transform.position = pts.Length > 0
            ? pts[Random.Range(0, pts.Length)].transform.position
            : spawnPoint;
        controller.enabled = true;
        velocityY = 0f;
        isClimbing = false;
        wasClimbing = false;
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
        climbSurfaceTeam = s.climbing ? s.team : 0; // keep viewmodel "in own ink" logic happy
        // surfaceTeam is computed locally every frame by GetInkTeam (runs for remotes too).

        // Match the controller capsule to the form so viewmodels/visuals scale correctly.
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
                Vector3 wallRight = Vector3.Cross(Vector3.up, climbNormal).normalized;
                Vector3 wallUp    = Vector3.Cross(climbNormal, wallRight).normalized;
                moveDir = (wallUp * currentDir.y - wallRight * currentDir.x).normalized;
                upHint  = climbNormal;
            }
            else
            {
                moveDir = (transform.forward * currentDir.y + transform.right * currentDir.x).normalized;
                upHint  = Vector3.up;
            }
            if (moveDir.sqrMagnitude > 0.001f)
                ViewmodelSquid.transform.rotation = Quaternion.LookRotation(moveDir, upHint);
        }
    }
    void UpdateSquidTrail()
    {
        if (SquidTrail == null) return;
        float moveMag = currentDir.magnitude;
        bool show     = swimMode && IsInInk && moveMag > 0.05f;

        SquidTrail.SetActive(show);
        if (!show) return;

        Vector3 moveDir, upHint;
        if (effectivelyClimbing)
        {
            Vector3 wallRight = Vector3.Cross(Vector3.up, climbNormal).normalized;
            Vector3 wallUp    = Vector3.Cross(climbNormal, wallRight).normalized;
            moveDir = (wallUp * currentDir.y - wallRight * currentDir.x).normalized;
            upHint  = climbNormal; // trail lies flat on wall face
        }
        else
        {
            moveDir = (transform.forward * currentDir.y + transform.right * currentDir.x).normalized;
            upHint  = Vector3.up; // trail lies flat on floor
        }

        if (moveDir.sqrMagnitude > 0.001f)
            SquidTrail.transform.rotation = Quaternion.LookRotation(moveDir, upHint);

        SquidTrail.transform.localScale = Vector3.one * moveMag;
    }

    void UpdateInk()
    {
        if (playerMode != PlayerMode.Client) return;
        bool inOwnInk = team != 0 && surfaceTeam == team;
        float rate = swimMode && inOwnInk ? inkRechargeRateSquid : inkRechargeRate;
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

    public void SetClimbContact(Vector3 normal, int surfTeam)
    {
        isClimbing = true;
        climbNormal = normal;
        climbSurfaceTeam = surfTeam;
    }

    public void ClearClimbContact()
    {
        isClimbing = false;
    }

    void OnDestroy()
    {
        if (playerMode == PlayerMode.Client)
            SteamGlobal.OnNetTick -= NetTickBroadcast;
    }

    // Fires off the Unity main thread at the network tick rate. Only reads the cached
    // snapshot (a reference captured from the last main-thread frame) and hands it to
    // the transport — no Unity object access here.
    void NetTickBroadcast(uint tick)
    {
        PlayerStateData snap = cachedNetState;
        if (snap == null) return;
        snap.tick = tick;
        SteamGlobal.SendAllData((ushort)NetMsg.PlayerState, snap);
    }

    void Jump(){
        if (grounded || IsInInk) { velocityY += jump * 2; }
    }
    void TestCheckScores(){
        FindFirstObjectByType<NetGameManager>().GetScores();
    }
    void GetInkTeam(){
        RaycastHit inkHit;
        if (Physics.Raycast(transform.position + controller.center, Vector3.down, out inkHit, (controller.height / 2) + 0.3f)){
            Debug.DrawLine(transform.position + controller.center, inkHit.point, Color.blue);
            grounded = true;
            try{
                surfaceTeam = inkHit.collider.gameObject.GetComponent<SurfaceInkManager>().getSurfaceTeam(inkHit.textureCoord);
            }
            catch{
                //Debug.Log("No Ink Team");
            }
            
        }else{
            Debug.DrawRay(transform.position + controller.center, Vector3.down * ((controller.height / 2) + 0.3f), Color.red);
            grounded = false;
            surfaceTeam = 0;
        }
    }
}
public enum PlayerMode
{
    Client, Network
}

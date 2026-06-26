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
    [SerializeField] Vector3 velocity;
    [SerializeField] GameObject ViewmodelPlayer, ViewmodelSquid, SquidTrail, InkTankScaler;
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
    float realSpeed, cameraPitch = 0.0f, velocityY = 0.0f;
    CharacterController controller = null;
    ControlLayer input;
    [SerializeField] Material mat;
    bool grounded;
    bool isClimbing;
    bool wasClimbing;
    Vector3 climbNormal;
    int climbSurfaceTeam;
    Vector3 RayDir, Pos;
    Vector2 currentDir = Vector2.zero, currentDirVelocity = Vector2.zero, currentMouseDelta = Vector2.zero, currentMouseDeltaVelocity = Vector2.zero, targetDir;

    void Start()
    {
        input = new ControlLayer();
        input.Enable();
        input.Movement.Jump.performed += ctx => Jump();
        input.Movement.Debug.performed += ctx => TestCheckScores();
        controller = GetComponent<CharacterController>();
        RayDir = transform.TransformDirection(Vector3.down);
        if (playerMode == PlayerMode.Client && lockCursor){ Cursor.lockState = CursorLockMode.Locked; Cursor.visible = false; }
        if(playerMode == PlayerMode.Client)
        {
            if(uiController == null)
            {
                uiController = FindFirstObjectByType<UIController>();
            }
        }
    }

    void Update()
    {
        UpdateMouseLook();
        UpdateMovement();
        GetInkTeam();
        UpdateInk();
        UpdateViewmodels();
        UpdateSquidTrail();
        //get the distance from the camera to this object
        float distance = Vector3.Distance(Camera.main.transform.position, transform.position);
        if(distance < 2){
            mat.color = new Color(1, 1, 1, Mathf.Clamp(distance / 2,0,1));
        }
        else{
            mat.color = new Color(1, 1, 1, 1);
        }
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
            realSpeed = swimMode ? swimSpeed - 2 : moveSpeed;
        }
        targetDir = input.Movement.Move.ReadValue<Vector2>(); targetDir.Normalize();
        currentDir = Vector2.SmoothDamp(currentDir, targetDir, ref currentDirVelocity, moveSmoothTime);

        bool climbing = swimMode && isClimbing && climbSurfaceTeam == team;
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
        if (controller.isGrounded){ velocityY = 0.0f; }
        Pos = transform.position;

        if (velocityY > 10){ velocityY = 10; }
        velocity = (transform.forward * currentDir.y + transform.right * currentDir.x) * realSpeed + Vector3.up * velocityY;
        controller.Move(velocity * Time.deltaTime);

        if(transform.position.y < -10){
            transform.position = new Vector3(0, 3, 0);
        }
    }

    [SerializeField] float viewmodelSwitchSpeed = 12f;
    void UpdateViewmodels()
    {
        if (ViewmodelPlayer == null || ViewmodelSquid == null) return;
        bool inOwnInk = team != 0 && swimMode &&
            (surfaceTeam == team || (isClimbing && climbSurfaceTeam == team) && !grounded);

        Vector3 playerTarget = swimMode ? Vector3.zero : Vector3.one;
        Vector3 squidTarget  = (swimMode && !inOwnInk) ? Vector3.one : Vector3.zero;

        ViewmodelPlayer.transform.localScale = Vector3.Lerp(
            ViewmodelPlayer.transform.localScale, playerTarget, Time.deltaTime * viewmodelSwitchSpeed);
        ViewmodelSquid.transform.localScale = Vector3.Lerp(
            ViewmodelSquid.transform.localScale, squidTarget, Time.deltaTime * viewmodelSwitchSpeed);
    }
    void UpdateSquidTrail()
    {
        if (SquidTrail == null) return;

        bool inOwnInk = team != 0 && (surfaceTeam == team || (isClimbing && climbSurfaceTeam == team));
        float moveMag = currentDir.magnitude;
        bool show     = swimMode && inOwnInk && moveMag > 0.05f;

        SquidTrail.SetActive(show);
        if (!show) return;

        Vector3 moveDir, upHint;
        if (isClimbing && climbSurfaceTeam == team)
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
        if (inkLevel < amount || swimMode) return false;
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

    void Jump(){
        if (grounded) { velocityY += jump * 2; }
    }
    void TestCheckScores(){
        FindObjectOfType<GameManager>().GetScores();
    }
    void GetInkTeam(){
        RaycastHit inkHit;
        if (Physics.Raycast(transform.position + controller.center, Vector3.down, out inkHit, (controller.height / 2) + 0.3f)){
            Debug.DrawLine(transform.position + controller.center, inkHit.point, Color.blue);
            grounded = true;
            try{
                int team = inkHit.collider.gameObject.GetComponent<SurfaceInkManager>().getSurfaceTeam(inkHit.textureCoord);
                if (team == 0){ /*Debug.Log("NoTeam");*/ surfaceTeam = 0; }
                else if (team == 1){ /*Debug.Log("AlphaTeam");*/ surfaceTeam = 1; }
                else if (team == 2){/* Debug.Log("BetaTeam");*/ surfaceTeam = 2;}
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

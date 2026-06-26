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
    [SerializeField] float gravity = -13.0f;
    [SerializeField] float jump = 8.0f;
    [SerializeField][Range(0.0f, 0.5f)] float moveSmoothTime = 0.3f;
    [SerializeField][Range(0.0f, 0.5f)] float mouseSmoothTime = 0.03f;
    [SerializeField] float floorDistance = 1;
    [SerializeField] bool lockCursor = true, gyro;
    [SerializeField] Vector3 velocity;
    [SerializeField] GameObject ViewmodelPlayer, ViewmodelSquid;
    int surfaceTeam = 0;
    bool squid;
    float realSpeed, cameraPitch = 0.0f, velocityY = 0.0f;
    CharacterController controller = null;
    ControlLayer input;
    [SerializeField] Material mat;
    bool mainHit;
    bool grounded;
    bool isClimbing;
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
        if (lockCursor){ Cursor.lockState = CursorLockMode.Locked; Cursor.visible = false; }
    }

    void Update()
    {
        UpdateMouseLook();
        UpdateMovement();
        GetInkTeam();
        UpdateViewmodels();
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
        Vector2 targetMouseDelta = input.Movement.Look.ReadValue<Vector2>();
        currentMouseDelta = Vector2.SmoothDamp(currentMouseDelta, targetMouseDelta, ref currentMouseDeltaVelocity, mouseSmoothTime);
        cameraPitch -= currentMouseDelta.y * mouseSensitivity;
        cameraPitch = Mathf.Clamp(cameraPitch, -70.0f, 80.0f);
        playerCamera.localEulerAngles = Vector3.right * cameraPitch;
        transform.Rotate(Vector3.up * currentMouseDelta.x * mouseSensitivity);
    }
    void UpdateMovement()
    {
        if(input.Movement.Squidmode.ReadValue<float>() != 0){
            squid = true;
            controller.height = 0.1f;
            controller.radius = 0.1f;
        }
        else{
            squid = false;
            isClimbing = false; // dismount wall when leaving squid mode
            controller.height = 1.92f;
            controller.radius = 0.5f;
        }
        realSpeed = 6;
        if(surfaceTeam != 0){
            if(surfaceTeam == team){
                if(squid){ realSpeed = 10; }
                else{ realSpeed = 6; }
            }
            else{
                if(squid){ realSpeed = 3; }
                else{ realSpeed = 4; }
            }
        }
        else{
            if(squid){ realSpeed = 4; }
            else{ realSpeed = 6; }
        }
        targetDir = input.Movement.Move.ReadValue<Vector2>(); targetDir.Normalize();
        currentDir = Vector2.SmoothDamp(currentDir, targetDir, ref currentDirVelocity, moveSmoothTime);

        bool climbing = squid && isClimbing && climbSurfaceTeam == team;
        if (climbing)
        {
            controller.slopeLimit = 90f;
            controller.stepOffset = 0f; // prevent step logic from fighting wall movement
            velocityY = 0f;
            // Map stick axes to wall-plane directions: Y = up the wall, X = across it
            Vector3 wallRight = Vector3.Cross(Vector3.up, climbNormal).normalized;
            Vector3 wallUp    = Vector3.Cross(climbNormal, wallRight).normalized;
            velocity = (wallUp * currentDir.y + -wallRight * currentDir.x) * realSpeed
                     - climbNormal * 2f; // constant push into wall to stay in contact
            controller.Move(velocity * Time.deltaTime);
            return;
        }

        controller.slopeLimit = 45f;
        controller.stepOffset = squid ? 0f : 0.3f; // disable step logic in squid mode to avoid "bouncing" off walls
        velocityY += (gravity * 3) * Time.deltaTime;
        if (controller.isGrounded){ velocityY = 0.0f; }
        Pos = transform.position;
        mainHit = Physics.SphereCast(Pos, controller.radius, RayDir, out RaycastHit hit, floorDistance);

        if (velocityY > 10){ velocityY = 10; }
        velocity = (transform.forward * currentDir.y + transform.right * currentDir.x) * realSpeed + Vector3.up * velocityY;
        controller.Move(velocity * Time.deltaTime);
    }

    [SerializeField] float viewmodelSwitchSpeed = 12f;
    void UpdateViewmodels()
    {
        if (ViewmodelPlayer == null || ViewmodelSquid == null) return;
        bool inOwnInk = team != 0 && squid &&
            (surfaceTeam == team || (isClimbing && climbSurfaceTeam == team) && !grounded);

        Vector3 playerTarget = squid ? Vector3.zero : Vector3.one;
        Vector3 squidTarget  = (squid && !inOwnInk) ? Vector3.one : Vector3.zero;

        ViewmodelPlayer.transform.localScale = Vector3.Lerp(
            ViewmodelPlayer.transform.localScale, playerTarget, Time.deltaTime * viewmodelSwitchSpeed);
        ViewmodelSquid.transform.localScale = Vector3.Lerp(
            ViewmodelSquid.transform.localScale, squidTarget, Time.deltaTime * viewmodelSwitchSpeed);
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
            //surfaceTeam = 0;
        }
    }
}

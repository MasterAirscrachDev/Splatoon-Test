using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

public class PlayerController : MonoBehaviour
{
    [SerializeField] Transform playerCamera = null;
    [SerializeField] int team = 0;
    [SerializeField] float mouseSensitivity = 3.5f;
    [SerializeField] float gravity = -13.0f;
    [SerializeField] float jump = 8.0f;
    [SerializeField][Range(0.0f, 0.5f)] float moveSmoothTime = 0.3f;
    [SerializeField][Range(0.0f, 0.5f)] float mouseSmoothTime = 0.03f;
    [SerializeField] float floorDistance = 1;
    [SerializeField] bool lockCursor = true, gyro;
    [SerializeField] Vector3 velocity;
    [SerializeField]int surfaceTeam = 0;
    bool squid;
    float realSpeed, cameraPitch = 0.0f, velocityY = 0.0f;
    CharacterController controller = null;
    ControlLayer input;
    [SerializeField] Material mat;

    RaycastHit hit, groundInfo;
    bool mainHit;
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
        }
        else{
            squid = false;
            controller.height = 1.92f;
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
        //Debug.Log(targetDir);
        currentDir = Vector2.SmoothDamp(currentDir, targetDir, ref currentDirVelocity, moveSmoothTime);
        velocityY += (gravity * 3) * Time.deltaTime;
        if (controller.isGrounded){ velocityY = 0.0f; }
        Pos = transform.position; var yes = -(((transform.localScale.y / 2) * controller.height) - 0.1f);
        Debug.DrawRay(Pos, Vector3.down * floorDistance, Color.red);
        mainHit = Physics.SphereCast(Pos, controller.radius, RayDir, out hit, floorDistance);
        //fc = Physics.Raycast(Pos, RayDir, out groundInfo, floorDistance);

        if (velocityY > 10){ velocityY = 10; }
        velocity = (transform.forward * currentDir.y + transform.right * currentDir.x) * realSpeed + Vector3.up * velocityY;
        controller.Move(velocity * Time.deltaTime);
    }
    void Jump(){
        if (mainHit) { velocityY += jump * 2; }
    }
    void TestCheckScores(){
        FindObjectOfType<GameManager>().GetScores();
    }
    void GetInkTeam(){
        if (Physics.Raycast(transform.position + Vector3.up, Vector3.down, out hit, 3)){
            try{
                int team = hit.collider.gameObject.GetComponent<SurfaceInkManager>().getSurfaceTeam(hit.textureCoord);
                if (team == 0){ Debug.Log("NoTeam"); surfaceTeam = 0; }
                else if (team == 1){ Debug.Log("AlphaTeam"); surfaceTeam = 1; }
                else if (team == 2){ Debug.Log("BetaTeam"); surfaceTeam = 2;}
            }
            catch{
                Debug.Log("No Ink Team");
            }
            
        }
    }
}

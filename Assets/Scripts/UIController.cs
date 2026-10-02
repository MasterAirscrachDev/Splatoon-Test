using UnityEngine;
using UnityEngine.UI;

// The ink tank shown beside the player in swim form. With a tank material (UI/InkTank) the fill is
// drawn by the shader: the ink level, a mark at the sub's cost, sloshing when the level jumps, the
// surface tipping as the camera turns, and bubbles while it refills. Without one, the fill image is
// scaled instead.
public class UIController : MonoBehaviour
{
    [SerializeField] Transform SquidUI, InkTankScaler;
    [SerializeField] float squidUISpeed = 8f;
    [SerializeField] Material inkTankMaterial; // UI/InkTank, on the image under InkTankScaler

    [Header("Ink motion")]
    [SerializeField] float sloshPerFill = 0.5f;     // extra wave height for each full tank the level jumps
    [SerializeField] float sloshMax = 0.06f;
    [SerializeField] float sloshDecay = 2.5f;       // per second
    [SerializeField] float tiltPerTurn = 0.0012f;   // surface slope per degree/second of camera turn
    [SerializeField] float tiltMax = 0.3f;
    [SerializeField] float tiltSpring = 90f, tiltDamping = 7f; // springy, so it wobbles back level
    [SerializeField] float bubblesAtRefill = 0.25f; // refill rate (tanks/second) for full bubbles

    static readonly int FillId = Shader.PropertyToID("_Fill");
    static readonly int MarkId = Shader.PropertyToID("_Mark");
    static readonly int SloshId = Shader.PropertyToID("_Slosh");
    static readonly int TiltId = Shader.PropertyToID("_Tilt");
    static readonly int BubblesId = Shader.PropertyToID("_Bubbles");

    bool swimMode;
    Image inkTankImage;
    Material tankMaterial; // our instance
    RectTransform squidUIRect;
    float ink = 1f, shownInk = 1f, slosh, tilt, tiltSpeed, bubbles, lastYaw;

    public float Fill => ink;                 // tests
    public Material TankMaterial => tankMaterial;

    void Start()
    {
        squidUIRect = SquidUI != null ? SquidUI.GetComponent<RectTransform>() : null;
        if (Camera.main != null) lastYaw = Camera.main.transform.eulerAngles.y;
        FindTankImage();
        if (inkTankImage != null && inkTankMaterial != null)
        {
            tankMaterial = new Material(inkTankMaterial);
            inkTankImage.material = tankMaterial;
            InkTankScaler.localScale = Vector3.one; // the shader shows the level
        }
    }

    void OnDestroy()
    {
        if (tankMaterial != null) Destroy(tankMaterial);
    }

    void FindTankImage()
    {
        if (inkTankImage == null && InkTankScaler != null)
            inkTankImage = InkTankScaler.GetComponentInChildren<Image>();
    }

    public void SetSwimMode(bool swim) => swimMode = swim;

    public void SetInkLevel(float level)
    {
        ink = level;
        if (tankMaterial != null || InkTankScaler == null) return;
        Vector3 s = InkTankScaler.localScale;
        s.y = level;
        InkTankScaler.localScale = s;
    }

    public void SetTeamColor(Color c)
    {
        FindTankImage();
        if (inkTankImage != null) inkTankImage.color = c;
    }

    void Update()
    {
        AnimateTank();
        if (SquidUI == null) return;

        Vector3 target = swimMode ? Vector3.one : Vector3.zero;
        SquidUI.localScale = Vector3.Lerp(SquidUI.localScale, target, Time.deltaTime * squidUISpeed);

        if (swimMode && squidUIRect != null)
        {
            PlayerController player = NetGameManager.LocalPlayer;
            Camera cam = Camera.main;
            if (player != null && cam != null)
            {
                Vector3 screenPos = cam.WorldToScreenPoint(player.transform.position + Vector3.up);
                if (screenPos.z > 0)
                    squidUIRect.position = screenPos;
            }
        }
    }

    void AnimateTank()
    {
        float dt = Time.deltaTime;
        if (tankMaterial == null || dt <= 0f) return;

        // Jumps in the level (a shot, a sub, a refill) slosh it; refilling bubbles.
        float change = ink - shownInk;
        shownInk = ink;
        slosh = Mathf.Min(sloshMax, slosh * Mathf.Exp(-sloshDecay * dt) + Mathf.Abs(change) * sloshPerFill);
        bubbles = Mathf.MoveTowards(bubbles, Mathf.Clamp01(change / dt / bubblesAtRefill), dt * 3f);

        // Turning tips the surface against the turn, then it wobbles back level.
        Camera cam = Camera.main;
        float yaw = cam != null ? cam.transform.eulerAngles.y : lastYaw;
        float turn = Mathf.DeltaAngle(lastYaw, yaw) / dt;
        lastYaw = yaw;
        float lean = Mathf.Clamp(-turn * tiltPerTurn, -tiltMax, tiltMax);
        tiltSpeed += ((lean - tilt) * tiltSpring - tiltSpeed * tiltDamping) * dt;
        tilt = Mathf.Clamp(tilt + tiltSpeed * dt, -tiltMax, tiltMax);

        PlayerLoadout loadout = PlayerLoadout.Local;
        tankMaterial.SetFloat(FillId, ink);
        tankMaterial.SetFloat(MarkId, loadout != null ? loadout.SubInkCost : 0f);
        tankMaterial.SetFloat(SloshId, slosh);
        tankMaterial.SetFloat(TiltId, tilt);
        tankMaterial.SetFloat(BubblesId, bubbles);
    }
}

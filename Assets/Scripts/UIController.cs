using UnityEngine;
using UnityEngine.UI;

public class UIController : MonoBehaviour
{
    [SerializeField] Transform SquidUI, InkTankScaler;
    [SerializeField] float squidUISpeed = 8f;

    bool swimMode;
    Image inkTankImage;
    RectTransform squidUIRect;

    void Start()
    {
        squidUIRect = SquidUI != null ? SquidUI.GetComponent<RectTransform>() : null;
    }

    public void SetSwimMode(bool swim) => swimMode = swim;

    public void SetInkLevel(float level)
    {
        if (InkTankScaler == null) return;
        Vector3 s = InkTankScaler.localScale;
        s.y = level;
        InkTankScaler.localScale = s;
    }

    public void SetTeamColor(Color c)
    {
        if (inkTankImage == null && InkTankScaler != null)
            inkTankImage = InkTankScaler.GetComponentInChildren<Image>();
        if (inkTankImage != null) inkTankImage.color = c;
    }

    void Update()
    {
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
}

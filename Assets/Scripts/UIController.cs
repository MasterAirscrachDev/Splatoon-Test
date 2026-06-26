using UnityEngine;

public class UIController : MonoBehaviour
{
    [SerializeField] Transform SquidUI, InkTankScaler;
    [SerializeField] float squidUISpeed = 8f;

    bool swimMode;

    public void SetSwimMode(bool swim) => swimMode = swim;

    public void SetInkLevel(float level)
    {
        if (InkTankScaler == null) return;
        Vector3 s = InkTankScaler.localScale;
        s.y = level;
        InkTankScaler.localScale = s;
    }

    void Update()
    {
        if (SquidUI == null) return;
        Vector3 target = swimMode ? Vector3.one : Vector3.zero;
        SquidUI.localScale = Vector3.Lerp(SquidUI.localScale, target, Time.deltaTime * squidUISpeed);
    }
}

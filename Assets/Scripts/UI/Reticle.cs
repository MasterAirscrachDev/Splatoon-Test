using UnityEngine;

// The aim marks. The aligned one, a grey ring, sits where the aim points (PlayerController.AimRaise
// above the view's centre) and never moves. The true one shows where a shot would really go: on the
// ring, unless something between the weapon and the aim point is in the way (a wall up close, a
// ledge when aiming down), when it moves to that. Weapons that throw or swing (no ShotOrigin) keep
// it on the ring. Faint in swim form; hidden while splatted, Super Jumping, spectating or in a menu.
public class Reticle : MonoBehaviour
{
    [SerializeField] RectTransform aligned, trueMark;
    [SerializeField] CanvasGroup group;
    [SerializeField] float swimAlpha = 0.35f;

    RectTransform area;
    PlayerController player;
    CameraAim camAim;
    Camera view;

    public RectTransform Aligned => aligned;   // tests
    public RectTransform TrueMark => trueMark;
    public bool Blocked { get; private set; }  // the true mark's off the ring: something's in the way
    public Vector3 TruePoint { get; private set; }
    public bool Shown => group != null && group.alpha > 0f;

    void Awake() => area = (RectTransform)transform;

    void LateUpdate()
    {
        PlayerController local = NetGameManager.LocalPlayer;
        if (local != player)
        {
            player = local;
            camAim = player != null ? player.GetComponentInChildren<CameraAim>(true) : null;
            view = camAim != null ? camAim.GetComponent<Camera>() : null;
            if (view == null && camAim != null) view = camAim.GetComponentInChildren<Camera>();
            if (view == null && player != null && player.CameraRig != null) view = player.CameraRig.GetComponentInChildren<Camera>();
        }
        bool show = player != null && view != null && !player.IsDead && !player.IsRespawning && !player.IsSuperJumping
                    && player.Role != PlayerRole.Spectator && !InputGate.Blocked;
        group.alpha = !show ? 0f : player.IsSquid ? swimAlpha : 1f;
        if (!show) return;

        Transform eye = view.transform;
        Vector3 aim = Quaternion.AngleAxis(-PlayerController.AimRaise, eye.right) * eye.forward;
        Vector3 alignedScreen = view.WorldToScreenPoint(eye.position + aim * 100f);
        Vector3 trueScreen = alignedScreen;
        Blocked = false;
        TruePoint = camAim != null ? camAim.target : eye.position + aim * 100f;

        PlayerLoadout loadout = player.GetComponent<PlayerLoadout>();
        Transform origin = loadout != null && loadout.CurrentWeapon != null ? loadout.CurrentWeapon.ShotOrigin : null;
        if (origin != null && camAim != null && camAim.target != Vector3.zero)
        {
            Vector3 from = origin.position, to = camAim.target - from;
            float length = to.magnitude;
            if (length > 0.1f && Physics.Raycast(from, to / length, out RaycastHit hit, length - 0.1f, PhysicsLayers.Environment, QueryTriggerInteraction.Ignore))
            {
                Blocked = true;
                TruePoint = hit.point;
                Vector3 s = view.WorldToScreenPoint(hit.point);
                if (s.z > 0f) trueScreen = s;
            }
        }
        Place(aligned, alignedScreen);
        Place(trueMark, trueScreen);
    }

    void Place(RectTransform mark, Vector3 screen)
    {
        if (RectTransformUtility.ScreenPointToLocalPointInRectangle(area, screen, null, out Vector2 local))
            mark.anchoredPosition = local;
    }
}

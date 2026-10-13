using UnityEngine;

// The aim marks. The aligned one, a grey ring, sits where the aim points (PlayerController.AimRaise
// above the view's centre) and never moves. The true one shows where a shot would really go: on the
// ring, unless something between the weapon and the aim point is in the way (a wall up close, a
// ledge when aiming down), when it moves to that. Weapons that throw or swing (no ShotOrigin) keep
// it on the ring. A weapon that works out its own aim (the Sniper: AimEnd) puts it where that ends.
// On a charging weapon the true ring fills round, clockwise from the top, in the team colour, as it
// charges. Hidden in swim form, while splatted, Super Jumping, spectating or in a menu.
public class Reticle : MonoBehaviour
{
    [SerializeField] RectTransform aligned, trueMark;
    [SerializeField] CanvasGroup group;

    RectTransform area;
    PlayerController player;
    CameraAim camAim;
    Camera view;

    public RectTransform Aligned => aligned;   // tests
    public RectTransform TrueMark => trueMark;
    public bool Blocked { get; private set; }  // the true mark's off the ring: something's in the way
    public Vector3 TruePoint { get; private set; }
    public bool Shown => group != null && group.alpha > 0f;
    public float ChargeFill => chargeFill != null && chargeFill.enabled ? chargeFill.fillAmount : 0f; // tests

    UnityEngine.UI.Image chargeFill;

    void Awake()
    {
        area = (RectTransform)transform;
        chargeFill = MakeChargeFill();
    }

    // A copy of the true ring's image over it, filled round as a charge builds.
    UnityEngine.UI.Image MakeChargeFill()
    {
        if (trueMark == null || !trueMark.TryGetComponent(out UnityEngine.UI.Image ring)) return null;
        var go = new GameObject("ChargeFill", typeof(RectTransform), typeof(UnityEngine.UI.Image));
        var rt = (RectTransform)go.transform;
        rt.SetParent(trueMark, false);
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = rt.offsetMax = Vector2.zero;
        var fill = go.GetComponent<UnityEngine.UI.Image>();
        fill.sprite = ring.sprite;
        fill.raycastTarget = false;
        fill.type = UnityEngine.UI.Image.Type.Filled;
        fill.fillMethod = UnityEngine.UI.Image.FillMethod.Radial360;
        fill.fillOrigin = (int)UnityEngine.UI.Image.Origin360.Top;
        fill.fillClockwise = true;
        fill.enabled = false;
        return fill;
    }

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
                    && player.Role != PlayerRole.Spectator && !InputGate.Blocked && !player.IsSquid;
        group.alpha = show ? 1f : 0f;
        if (!show) return;

        Transform eye = view.transform;
        Vector3 aim = Quaternion.AngleAxis(-PlayerController.AimRaise, eye.right) * eye.forward;
        Vector3 alignedScreen = view.WorldToScreenPoint(eye.position + aim * 100f);
        Vector3 trueScreen = alignedScreen;
        Blocked = false;
        TruePoint = camAim != null ? camAim.target : eye.position + aim * 100f;

        PlayerLoadout loadout = player.GetComponent<PlayerLoadout>();
        Weapon weapon = loadout != null ? loadout.CurrentWeapon : null;
        Transform origin = weapon != null ? weapon.ShotOrigin : null;
        if (weapon != null && weapon.AimEnd(out Vector3 end))
        {
            // Its own aim: where that ends (off the ring when it can't reach the cursor).
            TruePoint = end;
            Vector3 s = view.WorldToScreenPoint(end);
            if (s.z > 0f)
            {
                trueScreen = s;
                Blocked = ((Vector2)s - (Vector2)alignedScreen).sqrMagnitude > 4f;
            }
        }
        else if (origin != null && camAim != null && camAim.target != Vector3.zero)
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

        // A charging weapon's charge, round the true ring.
        float charge = weapon != null ? weapon.ChargeShown : -1f;
        if (chargeFill != null)
        {
            chargeFill.enabled = charge > 0f;
            chargeFill.fillAmount = Mathf.Clamp01(charge);
            Color c = player.TeamColour;
            c.a = 1f;
            chargeFill.color = c;
        }
    }

    void Place(RectTransform mark, Vector3 screen)
    {
        if (RectTransformUtility.ScreenPointToLocalPointInRectangle(area, screen, null, out Vector2 local))
            mark.anchoredPosition = local;
    }
}

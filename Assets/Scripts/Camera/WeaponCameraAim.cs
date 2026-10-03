using UnityEngine;

// Points the weapon at the owning player's camera aim target while they're aiming (firing); else it
// rests along the body, which faces the way they move. On remote copies (no camera of theirs here)
// it tilts with their synced view pitch while they aim.
public class WeaponCameraAim : MonoBehaviour
{
    PlayerController owner;
    CameraAim camAim;

    void Start()
    {
        owner = GetComponentInParent<PlayerController>();
        camAim = owner != null ? owner.GetComponentInChildren<CameraAim>(true) : null; // our own camera, not any player's
    }

    void Update()
    {
        if (owner != null && !owner.IsLocalPlayer)
        {
            Quaternion target = owner.Aiming ? Quaternion.Euler(owner.AimPitch, 0f, 0f) : Quaternion.identity;
            transform.localRotation = Quaternion.Slerp(transform.localRotation, target, 1f - Mathf.Exp(-20f * Time.deltaTime));
            return;
        }
        if (owner != null && !owner.Aiming) { transform.localRotation = Quaternion.identity; return; }
        Apply();
    }

    // Aim now (the weapon calls this just before a shot, so the first one goes where we look).
    public void Apply()
    {
        if (!enabled || camAim == null || (owner != null && !owner.IsLocalPlayer)) return;
        Debug.DrawLine(transform.position, camAim.target, Color.red);
        if (camAim.target != Vector3.zero) transform.LookAt(camAim.target);
        else transform.localRotation = Quaternion.identity;
    }
}

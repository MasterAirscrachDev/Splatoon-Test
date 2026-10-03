using UnityEngine;

// Points the weapon at the owning player's camera aim target while they're aiming (firing); else it
// rests along the body, which faces the way they move.
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
        if (owner != null && !owner.Aiming) { transform.localRotation = Quaternion.identity; return; }
        Apply();
    }

    // Aim now (the weapon calls this just before a shot, so the first one goes where we look).
    public void Apply()
    {
        if (!enabled || camAim == null) return;
        Debug.DrawLine(transform.position, camAim.target, Color.red);
        if (camAim.target != Vector3.zero) transform.LookAt(camAim.target);
        else transform.localRotation = Quaternion.identity;
    }
}

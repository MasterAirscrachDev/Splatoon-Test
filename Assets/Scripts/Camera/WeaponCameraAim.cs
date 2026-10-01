using UnityEngine;

// Points the weapon at the owning player's camera aim target.
public class WeaponCameraAim : MonoBehaviour
{
    CameraAim camAim;

    void Start()
    {
        PlayerController owner = GetComponentInParent<PlayerController>();
        camAim = owner != null ? owner.GetComponentInChildren<CameraAim>(true) : null; // our own camera, not any player's
    }

    void Update()
    {
        if (camAim == null) return;
        Debug.DrawLine(transform.position, camAim.target, Color.red);
        if (camAim.target != Vector3.zero) transform.LookAt(camAim.target);
        else transform.localRotation = Quaternion.identity;
    }
}

using UnityEngine;

// World point the camera is aiming at, for the weapon to point toward.
public class CameraAim : MonoBehaviour
{
    [SerializeField] LayerMask aimLayerMask;
    public Vector3 target;
    RaycastHit hit;
    float aimDistance = 10f;

    public void SetAimDistance(float distance)
    {
        aimDistance = distance;
    }

    void Update()
    {
        float camDistance = Mathf.Abs(transform.localPosition.z);
        Vector3 forward = Quaternion.AngleAxis(-PlayerController.AimRaise, transform.right) * transform.forward; // aimed a little above the view
        Physics.Raycast(transform.position, forward, out hit, aimDistance + camDistance, aimLayerMask);
        if (hit.transform != null) target = hit.point;
        else target = transform.position + forward * (aimDistance + camDistance);
    }
}

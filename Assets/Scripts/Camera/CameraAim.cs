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
        Physics.Raycast(transform.position, transform.forward, out hit, aimDistance + camDistance, aimLayerMask);
        if (hit.transform != null) target = hit.point;
        else target = transform.position + transform.forward * (aimDistance + camDistance);
    }
}

using UnityEngine;

// Pulls the camera in when level geometry is between it and the pivot.
public class CameraPush : MonoBehaviour
{
    [SerializeField] float distance = 5.0f;
    [SerializeField] new Transform camera;
    RaycastHit hit;

    void Update()
    {
        if (Physics.Raycast(transform.position, transform.TransformDirection(Vector3.back), out hit, distance,
                            PhysicsLayers.Environment, QueryTriggerInteraction.Ignore))
            camera.localPosition = new Vector3(0, 0, (hit.distance * -1) + 0.1f);
        else
            camera.localPosition = new Vector3(0, 0, distance * -1);
    }
}

using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class CameraAim : MonoBehaviour
{
    [SerializeField] LayerMask aimLayerMask;
    public Vector3 target;
    RaycastHit hit;
    float aimDistance = 10f;
    // Start is called before the first frame update
    public void SetAimDistance(float distance){
        aimDistance = distance;
    }

    // Update is called once per frame
    void Update()
    {
        //raycast forward
        float camDistance = Mathf.Abs(transform.localPosition.z);
        Physics.Raycast(transform.position, transform.forward, out hit, aimDistance + camDistance, aimLayerMask);
        if(hit.transform != null){
            target = hit.point;
        }
        else{
            target = transform.position + transform.forward * (aimDistance + camDistance);
        }

    }
}

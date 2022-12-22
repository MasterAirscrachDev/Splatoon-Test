using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class CameraAim : MonoBehaviour
{
    public Vector3 target;
    public float angle;
    RaycastHit hit;
    // Start is called before the first frame update
    void Start()
    {
        
    }

    // Update is called once per frame
    void Update()
    {
        //raycast forward
        Physics.Raycast(transform.position, transform.forward, out hit, 100);
        if(hit.transform != null){
            target = hit.point;
        }
        else{
            target = Vector3.zero;
            angle = transform.eulerAngles.x;
        }

    }
}

using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class CameraPush : MonoBehaviour
{
    [SerializeField] float distance = 5.0f;

    RaycastHit hit;
    [SerializeField] new Transform camera;
    void Update()   
    {
        if (Physics.Raycast(transform.position, transform.TransformDirection(Vector3.back), out hit, distance))
        { camera.localPosition = new Vector3(0, 0, hit.distance * -1); }
        else
        { camera.localPosition = new Vector3(0, 0, distance * -1); }
        //Debug.DrawRay(transform.position, transform.TransformDirection(Vector3.back) * distance); //Debug.Log(hit.distance);
    }
}

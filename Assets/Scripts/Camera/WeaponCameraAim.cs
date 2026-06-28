using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class WeaponCameraAim : MonoBehaviour
{
    CameraAim camAim;
    // Start is called before the first frame update
    void Start()
    {
        camAim = FindFirstObjectByType<CameraAim>();
    }

    // Update is called once per frame
    void Update()
    {
        Debug.DrawLine(transform.position, camAim.target, Color.red);
        if(camAim.target != Vector3.zero){
            transform.LookAt(camAim.target);
        }
        else{
            transform.localRotation = Quaternion.Euler(0, 0, 0);
        }
    }
}

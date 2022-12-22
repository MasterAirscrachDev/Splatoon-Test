using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class WeaponShooter : MonoBehaviour
{
    [SerializeField] int splashSize = 30, team = 1;
    [SerializeField] float fireRate = 0.5f, YradomRange = 0.0f, XradomRange = 0.0f, Range = 10;
    [SerializeField] GameObject projectile;
    Transform shotPoint;
    ControlLayer input;
    bool shooting = false;
    // Start is called before the first frame update
    void Start()
    {
        input = new ControlLayer();
        input.Enable();
        shotPoint = transform.GetChild(0);
        //StartCoroutine(Shoot());
        input.Weapon.Attack.performed += ctx => ShootPressed();
    }
    void ShootPressed(){
        if(!shooting){
            shooting = true;
            StartCoroutine(Shoot());
        }
    }
    
    IEnumerator Shoot(){
        //get a rotation thats the shot point's rotation + a random rotation along the y axis and x axis
        Vector3 angle = shotPoint.rotation.eulerAngles;
        angle.y += Random.Range(-YradomRange, YradomRange);
        angle.x += Random.Range(-XradomRange, XradomRange);

        GameObject s = Instantiate(projectile, shotPoint.position, Quaternion.Euler(angle));
        s.GetComponent<ProjectileSystem>().Setup(FindObjectOfType<PlayerController>().GetComponent<CharacterController>().velocity, Range, splashSize, team);
        yield return new WaitForSeconds(fireRate);
        if(input.Weapon.Attack.ReadValue<float>() != 0){ StartCoroutine(Shoot()); }
        else{ shooting = false; }
    }
}

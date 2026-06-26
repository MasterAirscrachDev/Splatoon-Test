using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class WeaponShooter : MonoBehaviour
{
    [SerializeField] int splashSize = 30, team = 1;
    [SerializeField] float fireRate = 0.5f, YradomRange = 0.0f, XradomRange = 0.0f, Range = 10;
    [SerializeField] float inkCostPerShot = 3f;
    [SerializeField] GameObject projectile;
    [SerializeField] PlayerController player;
    Transform shotPoint;
    ControlLayer input;
    float lastShotTime = 0f;

    void Start()
    {
        input = new ControlLayer();
        input.Enable();
        shotPoint = transform.GetChild(0);
    }
    void Update()
    {
        bool firing = input.Weapon.Attack.ReadValue<float>() != 0;
        if (firing && Time.time - lastShotTime >= fireRate && player.ConsumeInk(inkCostPerShot * 0.001f))
        {
            lastShotTime = Time.time;
            Vector3 angle = shotPoint.rotation.eulerAngles;
            angle.y += Random.Range(-YradomRange, YradomRange);
            angle.x += Random.Range(-XradomRange, XradomRange);

            GameObject s = Instantiate(projectile, shotPoint.position, Quaternion.Euler(angle));
            CharacterController cc = player != null ? player.GetComponent<CharacterController>() : null;
            s.GetComponent<ProjectileSystem>().Setup(cc != null ? cc.velocity : Vector3.zero, Range, splashSize, team);
        }
    }
}

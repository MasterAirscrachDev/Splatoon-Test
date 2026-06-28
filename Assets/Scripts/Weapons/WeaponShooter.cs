using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class WeaponShooter : MonoBehaviour
{
    [SerializeField] int splashSize = 30;
    [SerializeField] float fireRate = 0.5f, YradomRange = 0.0f, XradomRange = 0.0f, Range = 10;
    [SerializeField] float inkCostPerShot = 3f;
    [SerializeField] float extraShotSpread = 10f;
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
        if (player == null) player = GetComponentInParent<PlayerController>();
        if (player == null) Debug.LogError($"WeaponShooter on {gameObject.name} has no PlayerController in parent hierarchy.");
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
            Quaternion rot = Quaternion.Euler(angle);

            CharacterController cc = player.GetComponent<CharacterController>();
            Vector3 inherit = cc != null ? cc.velocity : Vector3.zero;

            // Main shot at full range
            SpawnShot(rot, Range, inherit, splashSize, true);

            // Extra shots with diminishing range to cover the floor between shooter and main shot
            int extras = Mathf.FloorToInt(Range / 2f);
            for (int i = 1; i <= extras; i++)
            {
                float r = Range * i / (extras + 1f);
                Quaternion noisyRot = rot * Quaternion.Euler(
                    Random.Range(-extraShotSpread, extraShotSpread),
                    Random.Range(-extraShotSpread, extraShotSpread),
                    0f);
                SpawnShot(noisyRot, r + Random.Range(-0.1f, 0.1f), inherit, Mathf.RoundToInt(splashSize * 0.7f), Random.value < 0.1f);
            }
        }
    }

    void SpawnShot(Quaternion rotation, float range, Vector3 inherit, int splashSize, bool visible)
    {
        Vector3 vel = inherit + rotation * Vector3.forward * range;
        GameObject s = Instantiate(projectile, shotPoint.position, rotation);
        s.GetComponent<ProjectileSystem>().Setup(vel, splashSize, player.Team, visible);
    }
}

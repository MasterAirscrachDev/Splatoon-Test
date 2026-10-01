using System.Collections.Generic;
using UnityEngine;

public class WeaponShooter : MonoBehaviour
{
    [SerializeField] int splashSize = 30;
    [SerializeField] float fireRate = 0.5f, YradomRange = 0.0f, XradomRange = 0.0f, Range = 10;
    [SerializeField] float inkCostPerShot = 3f; // x0.001 of a full tank
    [SerializeField] float extraShotSpread = 10f;
    [SerializeField] GameObject projectile;
    [SerializeField] PlayerController player;
    Transform shotPoint;
    ControlLayer input;
    CharacterController playerController;
    float lastShotTime = 0f;

    // Current volley, broadcast as one message so remotes can replay it.
    readonly List<float> volleyVelocities = new List<float>();
    readonly List<int> volleySizes = new List<int>();
    readonly List<bool> volleyVisible = new List<bool>();

    public GameObject ProjectilePrefab => projectile;

    void Start()
    {
        input = new ControlLayer();
        input.Enable();
        shotPoint = transform.GetChild(0);
        if (player == null) player = GetComponentInParent<PlayerController>();
        if (player == null) Debug.LogError($"WeaponShooter on {gameObject.name} has no PlayerController in parent hierarchy.");
        else playerController = player.GetComponent<CharacterController>();
    }

    void OnDestroy() => input?.Dispose();

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

            Vector3 inherit = playerController != null ? playerController.velocity : Vector3.zero;

            volleyVelocities.Clear();
            volleySizes.Clear();
            volleyVisible.Clear();

            SpawnShot(rot, Range, inherit, splashSize, true);

            // Shorter filler shots cover the floor between the shooter and the main shot.
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

            NetGameManager.Instance?.BroadcastShots(player, shotPoint.position,
                volleyVelocities.ToArray(), volleySizes.ToArray(), volleyVisible.ToArray());
        }
    }

    void SpawnShot(Quaternion rotation, float range, Vector3 inherit, int splashSize, bool visible)
    {
        Vector3 vel = inherit + rotation * Vector3.forward * range;
        ProjectilePool.Get(projectile, shotPoint.position, rotation)
                      .Setup(vel, splashSize, player.Team, visible, authoritative: true, ownerId: player.OwnerId);

        volleyVelocities.Add(vel.x); volleyVelocities.Add(vel.y); volleyVelocities.Add(vel.z);
        volleySizes.Add(splashSize);
        volleyVisible.Add(visible);
    }
}

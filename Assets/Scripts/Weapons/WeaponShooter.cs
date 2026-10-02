using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Serialization;

// Gun-type weapon: a main shot with random spread, shorter filler shots covering the ground in
// between, and ink dropped at the shooter's feet while firing. Stats live on each weapon prefab;
// shots fly by ProjectileManager, shaped by this weapon's Ballistics curves.
public class WeaponShooter : Weapon
{
    [Header("Shots")]
    [SerializeField] float fireRate = 0.1f;      // seconds between shots
    [SerializeField, FormerlySerializedAs("range")]
    float shotSpeed = 10f;                       // main shot's starting speed (fillers start slower)
    [SerializeField] int fillerShots = 5;        // shorter shots per volley, spaced between us and the main shot
    [SerializeField] Ballistics ballistics = new Ballistics(); // its speed and gravity over its flight
    [SerializeField] int splashSize = 11;
    [SerializeField] float damage = 30f;
    [SerializeField] float inkCostPerShot = 16f; // x0.001 of a full tank
    [SerializeField] GameObject projectile;
    [SerializeField] Transform muzzle;

    [Header("Spread (degrees)")]
    [SerializeField] float yawSpread = 15f;      // random aim wobble per shot
    [SerializeField] float pitchSpread = 2f;
    [SerializeField] float fillerSpread = 10f;

    [Header("Feet ink")] // keeps the shooter standing in their own ink
    [SerializeField] float feetShotInterval = 0.4f;
    [SerializeField] int feetShotMinSize = 12, feetShotMaxSize = 18;
    [SerializeField] float feetShotDropSpeed = 6f; // starts falling at once, so it lands under us even on the move

    ControlLayer input;
    CharacterController playerController;
    float lastShotTime = -1f, nextFeetShot;

    // Current volley, broadcast as one message so remotes can replay it.
    readonly List<float> volleyVelocities = new List<float>();
    readonly List<int> volleySizes = new List<int>();
    readonly List<bool> volleyVisible = new List<bool>();

    public GameObject ProjectilePrefab => projectile;
    public float FireRate => fireRate;
    public float ShotSpeed => shotSpeed;
    public Ballistics Ballistics => ballistics;
    public bool ScriptedFire { get; set; } // tests: hold the trigger
    public int FeetShots { get; private set; }
    public int MainShots { get; private set; }
    public Vector3 LastInherited { get; private set; } // the shooter's velocity carried by the last volley
    public event System.Action<Vector3, Quaternion> MainShotFired; // own velocity (no inherit), aim before spread (tests)

    void Start()
    {
        input = new ControlLayer();
        input.Enable();
        if (muzzle == null) muzzle = transform;
        if (Owner != null) playerController = Owner.GetComponent<CharacterController>();
    }

    void OnDestroy() { input?.Disable(); input?.Dispose(); }

    void Update()
    {
        PlayerController player = Owner;
        if (player == null) return;
        bool hardware = !player.IsTestPlayer && !InputGate.Blocked && input.Weapon.Attack.ReadValue<float>() != 0;
        bool firing = !InputGate.MatchLocked && (ScriptedFire || hardware);
        // Still shooting (not out of ink or in swim form): a shot went out within the last cycle.
        bool shooting = firing && lastShotTime >= 0f && Time.time - lastShotTime <= fireRate + 0.05f;
        if (shooting && Time.time >= nextFeetShot) DropFeetShot(player);
        if (firing && Time.time - lastShotTime >= fireRate && player.ConsumeInk(inkCostPerShot * 0.001f))
        {
            lastShotTime = Time.time;
            MainShots++;

            Vector3 angle = muzzle.rotation.eulerAngles;
            angle.y += Random.Range(-yawSpread, yawSpread);
            angle.x += Random.Range(-pitchSpread, pitchSpread);
            Quaternion rot = Quaternion.Euler(angle);

            // Running carries into the shots; jumping and falling don't.
            Vector3 inherit = playerController != null ? playerController.velocity : Vector3.zero;
            inherit.y = 0f;
            LastInherited = inherit;

            volleyVelocities.Clear();
            volleySizes.Clear();
            volleyVisible.Clear();

            Vector3 main = SpawnShot(player, rot, shotSpeed, inherit, splashSize, true);
            MainShotFired?.Invoke(main, muzzle.rotation);

            // Shorter filler shots cover the floor between the shooter and the main shot.
            for (int i = 1; i <= fillerShots; i++)
            {
                float r = shotSpeed * i / (fillerShots + 1f);
                Quaternion noisyRot = rot * Quaternion.Euler(
                    Random.Range(-fillerSpread, fillerSpread),
                    Random.Range(-fillerSpread, fillerSpread),
                    0f);
                SpawnShot(player, noisyRot, r + Random.Range(-0.1f, 0.1f), inherit, Mathf.RoundToInt(splashSize * 0.7f), Random.value < 0.1f);
            }

            NetGameManager.Instance?.BroadcastShots(player, muzzle.position, inherit,
                volleyVelocities.ToArray(), volleySizes.ToArray(), volleyVisible.ToArray());
        }
    }

    // An unseen shot released just above the feet, heading straight down. Not replayed on remotes:
    // its splat reaches them like any other.
    void DropFeetShot(PlayerController player)
    {
        nextFeetShot = Time.time + feetShotInterval;
        FeetShots++;
        Vector3 feet = player.transform.position + Vector3.up * 0.5f;
        ProjectileManager.Fire(projectile, feet, Vector3.down * feetShotDropSpeed, Random.Range(feetShotMinSize, feetShotMaxSize + 1), player.Team,
                               visible: false, ownerId: player.OwnerId, damage: damage, impactParticles: false, source: DisplayName);
    }

    // Returns its launch velocity (without what the shooter carries into it).
    Vector3 SpawnShot(PlayerController player, Quaternion rotation, float speed, Vector3 inherit, int size, bool visible)
    {
        Vector3 launch = rotation * Vector3.forward * speed;
        ProjectileManager.Fire(projectile, muzzle.position, launch, size, player.Team, visible,
                               ownerId: player.OwnerId, damage: damage, ballistics: ballistics, inherit: inherit, source: DisplayName);

        volleyVelocities.Add(launch.x); volleyVelocities.Add(launch.y); volleyVelocities.Add(launch.z);
        volleySizes.Add(size);
        volleyVisible.Add(visible);
        return launch;
    }
}

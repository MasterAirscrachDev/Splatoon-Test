using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Serialization;

// Gun-type weapon: a main shot with random spread, shorter filler shots covering the ground in
// between, and ink dropped at the shooter's feet while firing. Stats live on each weapon prefab;
// shots fly by ProjectileManager, shaped by this weapon's Ballistics curves.
public class WeaponShooter : Weapon
{
    [Header("Shots")]
    [SerializeField, Tooltip("Seconds between shots")] float fireRate = 0.1f;
    [SerializeField, FormerlySerializedAs("range"), Tooltip("The main shot's launch speed (metres per second; its Ballistics shape it from there). Fillers launch slower, spread between us and it")]
    float shotSpeed = 10f;
    [SerializeField, Tooltip("Shorter, slower shots per volley, spaced between us and the main shot: they ink the floor in between. They do no damage")]
    int fillerShots = 5;
    [SerializeField, Range(0f, 1f), Tooltip("Share of the filler shots that are drawn (the rest only paint): fewer is less visual noise")]
    float fillersSeen = 0.33f;
    [SerializeField, Tooltip("Speed and gravity over each shot's flight")] Ballistics ballistics = new Ballistics();
    [SerializeField, Tooltip("The main shot's splat size: the paint it leaves (fillers leave 70% of it). It also sets how big shots are drawn")]
    int splashSize = 11;
    [SerializeField, Tooltip("The main shot's damage, by how long it's flown")] DamageFalloff damage = new DamageFalloff(30f, DamageFalloff.HoldThenFall(0.25f, 0.5f, 0.5f));
    [SerializeField, Tooltip("Ink per shot, in thousandths of a full tank")] float inkCostPerShot = 16f;
    [SerializeField, Tooltip("Where shots leave from")] Transform muzzle;

    [Header("Spread (degrees)")]
    [SerializeField, Tooltip("Random aim wobble left and right on each volley (degrees, ±)")] float yawSpread = 15f;
    [SerializeField, Tooltip("Random aim wobble up and down on each volley (degrees, ±)")] float pitchSpread = 2f;
    [SerializeField, Tooltip("Extra random wobble on each filler shot, up/down and left/right (degrees, ±)")] float fillerSpread = 10f;

    [Header("Look")]
    [SerializeField, Tooltip("How the main shot is drawn")] ShotLook shotLook = new ShotLook();
    [SerializeField, Tooltip("How the (seen) filler shots are drawn")] ShotLook fillerLook = new ShotLook();

    ControlLayer input;
    CharacterController playerController;
    WeaponCameraAim cameraAim;
    float lastShotTime = -1f;

    // Current volley, broadcast as one message so remotes can replay it.
    readonly List<float> volleyVelocities = new List<float>();
    readonly List<int> volleySizes = new List<int>();
    readonly List<bool> volleyVisible = new List<bool>();

    public float FireRate => fireRate;
    public float ShotSpeed => shotSpeed;
    public Ballistics Ballistics => ballistics;
    public bool ScriptedFire { get; set; } // tests: hold the trigger
    public int MainShots { get; private set; }
    public Vector3 LastInherited { get; private set; } // the shooter's velocity carried by the last volley
    public override Transform ShotOrigin => muzzle;
    public event System.Action<Vector3, Quaternion> MainShotFired; // own velocity (no inherit), aim before spread (tests)

    void Start()
    {
        input = new ControlLayer();
        input.Enable();
        if (muzzle == null) muzzle = transform;
        if (Owner != null) playerController = Owner.GetComponent<CharacterController>();
        cameraAim = GetComponent<WeaponCameraAim>();
    }

    protected override void OnDestroy() { base.OnDestroy(); input?.Disable(); input?.Dispose(); }

    void Update()
    {
        PlayerController player = Owner;
        if (player == null) return;
        // Real controls only when nothing's scripting the player (tests drive it through ScriptedFire).
        bool hardware = !player.IsTestPlayer && player.ScriptedInput == null && !InputGate.Blocked && input.Weapon.Attack.ReadValue<float>() != 0;
        bool firing = !InputGate.MatchLocked && (ScriptedFire || hardware);
        player.AttackHeld = firing;
        if (firing && !player.IsSquid)
        {
            player.MarkAiming(); // face the view, and aim there before the shot
            if (cameraAim != null) cameraAim.Apply();
        }
        // Still shooting (not out of ink or in swim form): a shot went out within the last cycle.
        bool shooting = firing && lastShotTime >= 0f && Time.time - lastShotTime <= fireRate + 0.05f;
        UpdateFeetInk(player, shooting);
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

            Vector3 main = SpawnShot(player, rot, shotSpeed, inherit, splashSize, true, true, shotLook);
            MainShotFired?.Invoke(main, muzzle.rotation);

            // Shorter filler shots cover the floor between the shooter and the main shot.
            for (int i = 1; i <= fillerShots; i++)
            {
                float r = shotSpeed * i / (fillerShots + 1f);
                Quaternion noisyRot = rot * Quaternion.Euler(
                    Random.Range(-fillerSpread, fillerSpread),
                    Random.Range(-fillerSpread, fillerSpread),
                    0f);
                SpawnShot(player, noisyRot, r + Random.Range(-0.1f, 0.1f), inherit, Mathf.RoundToInt(splashSize * 0.7f), Random.value < fillersSeen, false, fillerLook);
            }

            NetGameManager.Instance?.BroadcastShots(player, muzzle.position, inherit,
                volleyVelocities.ToArray(), volleySizes.ToArray(), volleyVisible.ToArray());
        }
    }

    // Their volley, visual-only, flown with this weapon's ballistics.
    public override void Replay(ProjectileSpawnData d)
    {
        if (d.velocities == null || d.splashSizes == null || d.visible == null) return;
        Vector3 inherit = d.inherit != null ? (Vector3)d.inherit : Vector3.zero;
        int count = Mathf.Min(d.splashSizes.Length, d.visible.Length, d.velocities.Length / 3);
        for (int i = 0; i < count; i++)
        {
            Vector3 v = new Vector3(d.velocities[i * 3], d.velocities[i * 3 + 1], d.velocities[i * 3 + 2]);
            ProjectileManager.Fire(d.origin, v, d.splashSizes[i], d.team, d.visible[i],
                                   authoritative: false, ownerId: d.shooterSteamId, ballistics: ballistics, inherit: inherit,
                                   look: i == 0 ? shotLook : fillerLook); // (the main shot first)
        }
    }

    // Returns its launch velocity (without what the shooter carries into it).
    Vector3 SpawnShot(PlayerController player, Quaternion rotation, float speed, Vector3 inherit, int size, bool visible, bool dealDamage = true, ShotLook look = null)
    {
        Vector3 launch = rotation * Vector3.forward * speed;
        ProjectileManager.Fire(muzzle.position, launch, size, player.Team, visible,
                               ownerId: player.OwnerId, falloff: dealDamage ? damage : null, damage: 0f, ballistics: ballistics, inherit: inherit, source: DisplayName, look: look);

        volleyVelocities.Add(launch.x); volleyVelocities.Add(launch.y); volleyVelocities.Add(launch.z);
        volleySizes.Add(size);
        volleyVisible.Add(visible);
        return launch;
    }
}

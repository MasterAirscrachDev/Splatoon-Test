using UnityEngine;

// Blaster: one big, slow-firing shot that flies dead straight (no drop, no slowing) for a short
// range and explodes there in mid-air, or sooner on whatever it hits. A direct hit does heavy
// damage; the blast inks everything around it and hurts enemies in reach (lethal-ish core falling
// off to the edge). Running carries into the shot like the guns; jumping doesn't.
public class Blaster : Weapon
{
    [Header("Shot")]
    [SerializeField] float fireRate = 0.75f;          // seconds between shots
    [SerializeField] float shotSpeed = 30f;
    [SerializeField] float range = 6.5f;              // explodes after flying this far
    [SerializeField] float directDamage = 125f;       // to whoever it hits
    [SerializeField] float inkCostPerShot = 80f;      // x0.001 of a full tank
    [SerializeField] int shotSize = 22;               // how big the shot looks
    [SerializeField] float trailSpacing = 0.45f;      // the shot drips ink along its path this often (metres)
    [SerializeField] int trailSize = 9;               // each drip's splat
    [SerializeField] Ballistics ballistics = new Ballistics { gravityOverTime = AnimationCurve.Constant(0f, 1f, 0f) }; // no drop
    [SerializeField] GameObject projectile;
    [SerializeField] Transform muzzle;

    [Header("Blast")]
    [SerializeField] InkBlast blast = new InkBlast();

    public string Source => DisplayName; // "splatted with"

    ControlLayer input;
    CharacterController body;
    WeaponCameraAim cameraAim;
    float lastShotTime = -1f;

    public float FireRate => fireRate;
    public float ShotSpeed => shotSpeed;
    public float Range => range;
    public float DirectDamage => directDamage;
    public float TrailSpacing => trailSpacing;
    public InkBlast Blast => blast;
    public Transform Muzzle => muzzle;
    public bool ScriptedFire { get; set; } // tests: hold the trigger
    public int Shots { get; private set; }
    public Vector3 LastMuzzle { get; private set; }   // where and which way the last shot left (tests)
    public Vector3 LastLaunch { get; private set; }

    void Start()
    {
        input = new ControlLayer();
        input.Enable();
        if (muzzle == null) muzzle = transform;
        if (Owner != null) body = Owner.GetComponent<CharacterController>();
        cameraAim = GetComponent<WeaponCameraAim>();
    }

    void OnDestroy() { input?.Disable(); input?.Dispose(); }

    void Update()
    {
        PlayerController player = Owner;
        if (player == null) return;
        bool hardware = !player.IsTestPlayer && player.ScriptedInput == null && !InputGate.Blocked && input.Weapon.Attack.ReadValue<float>() != 0;
        bool firing = !InputGate.MatchLocked && (ScriptedFire || hardware);
        player.AttackHeld = firing;
        if (firing && !player.IsSquid)
        {
            player.MarkAiming(); // face the view, and aim there before the shot
            if (cameraAim != null) cameraAim.Apply();
        }
        // Still shooting (not out of ink or in swim form): a shot went out within the last cycle.
        UpdateFeetInk(player, firing && lastShotTime >= 0f && Time.time - lastShotTime <= fireRate + 0.05f, projectile);
        if (!firing || Time.time - lastShotTime < fireRate || !player.ConsumeInk(inkCostPerShot * 0.001f)) return;
        lastShotTime = Time.time;
        Shots++;

        Vector3 inherit = body != null ? body.velocity : Vector3.zero;
        inherit.y = 0f; // jumping and falling don't carry into it
        Vector3 launch = muzzle.forward * shotSpeed;
        LastMuzzle = muzzle.position;
        LastLaunch = launch;
        Fire(muzzle.position, launch, inherit, player.Team, player.OwnerId, authoritative: true);
        NetGameManager.Instance?.BroadcastShots(player, muzzle.position, inherit, new[] { launch.x, launch.y, launch.z }, new[] { shotSize }, new[] { true });
    }

    void Fire(Vector3 from, Vector3 launch, Vector3 inherit, int team, ulong ownerId, bool authoritative)
    {
        ProjectileManager.Fire(projectile, from, launch, shotSize, team, true, authoritative, ownerId, directDamage,
                               impactParticles: false, ballistics: ballistics, inherit: inherit, source: Source,
                               maxDistance: range, blast: blast, trailSpacing: trailSpacing, trailSize: trailSize);
    }

    // Their shot, visual-only here: it flies and explodes the same way.
    public override void Replay(ProjectileSpawnData d)
    {
        if (d.velocities == null || d.velocities.Length < 3 || projectile == null) return;
        Vector3 inherit = d.inherit != null ? (Vector3)d.inherit : Vector3.zero;
        Fire(d.origin, new Vector3(d.velocities[0], d.velocities[1], d.velocities[2]), inherit, d.team, d.shooterSteamId, authoritative: false);
    }
}

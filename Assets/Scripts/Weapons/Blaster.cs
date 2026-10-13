using UnityEngine;

// Blaster: one big, slow-firing shot that flies dead straight (no drop, no slowing) for a short
// range and explodes there in mid-air, or sooner on whatever it hits. A direct hit does heavy
// damage; the blast inks everything around it and hurts enemies in reach (lethal-ish core falling
// off to the edge). Running carries into the shot like the guns; jumping doesn't.
public class Blaster : Weapon
{
    [Header("Shot")]
    [SerializeField, Tooltip("Seconds between shots")] float fireRate = 0.75f;
    [SerializeField, Tooltip("Launch speed (metres per second)")] float shotSpeed = 30f;
    [SerializeField, Tooltip("It explodes after flying this far (metres), or on whatever it hits first")] float range = 6.5f;
    [SerializeField, Tooltip("Damage to whoever it hits directly, by how long it's flown (they don't also take the blast)")] DamageFalloff directDamage = new DamageFalloff(125f, DamageFalloff.HoldThenFall(0.2f, 0.3f, 0.8f));
    [SerializeField, Tooltip("Ink per shot, in thousandths of a full tank")] float inkCostPerShot = 80f;
    [SerializeField, Tooltip("How big the shot is drawn, as a splat size would be (it paints nothing itself: the blast and its trail do)")] int shotSize = 22;
    [SerializeField, Tooltip("It drips unseen ink under its path every this many metres")] float trailSpacing = 0.45f;
    [SerializeField, Tooltip("Each drip's splat size")] int trailSize = 9;
    [SerializeField, Tooltip("Speed and gravity over its flight (none by default: it flies straight)")] Ballistics ballistics = new Ballistics { gravityOverTime = AnimationCurve.Constant(0f, 1f, 0f) };
    [SerializeField, Tooltip("How the shot is drawn")] ShotLook shotLook = new ShotLook { size = 0.6f };
    [SerializeField, Tooltip("Where shots leave from")] Transform muzzle;

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
    public DamageFalloff DirectDamage => directDamage;
    public float TrailSpacing => trailSpacing;
    public InkBlast Blast => blast;
    public Transform Muzzle => muzzle;
    public override Transform ShotOrigin => muzzle;
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

    protected override void OnDestroy() { base.OnDestroy(); input?.Disable(); input?.Dispose(); }

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
        UpdateFeetInk(player, firing && lastShotTime >= 0f && Time.time - lastShotTime <= fireRate + 0.05f);
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
        ProjectileManager.Fire(from, launch, shotSize, team, true, authoritative, ownerId, 0f, falloff: directDamage,
                               splashes: false, look: shotLook, ballistics: ballistics, inherit: inherit, source: Source,
                               maxDistance: range, blast: blast, trailSpacing: trailSpacing, trailSize: trailSize);
    }

    // Their shot, visual-only here: it flies and explodes the same way.
    public override void Replay(ProjectileSpawnData d)
    {
        if (d.velocities == null || d.velocities.Length < 3) return;
        Vector3 inherit = d.inherit != null ? (Vector3)d.inherit : Vector3.zero;
        Fire(d.origin, new Vector3(d.velocities[0], d.velocities[1], d.velocities[2]), inherit, d.team, d.shooterSteamId, authoritative: false);
    }
}

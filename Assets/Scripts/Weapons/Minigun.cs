using UnityEngine;

// Minigun (a splatling): hold attack to charge, let go to fire a stream of shots for as long as it
// was charged (a full charge fires for burstTime, a partial one for that share of it). Slows us
// while charging and while firing. Variants, on the prefab:
//   requireFullCharge: only a full charge fires; letting go early just loses it.
//   chargeWhileFiring: holding attack mid-stream starts the next charge (which adds on to what's
//     left of the stream when let go); otherwise charging waits for the stream to end.
// Each shot flies like a gun's (its Ballistics) with shorter filler shots inking the floor between
// us and it (as the shooters' do), broadcast for other players to replay. The model's barrels (its
// "_Barrels" piece) spin up with the charge and spin while it fires.
public class Minigun : ChargeWeapon
{
    [Header("Stream")]
    [SerializeField, Tooltip("How long a full charge fires for (seconds); a partial charge fires for that share of it")] float burstTime = 2f;
    [SerializeField, Tooltip("Only a full charge fires: letting go early just loses it")] bool requireFullCharge = false;
    [SerializeField, Tooltip("Holding attack while still firing starts the next charge (added on to what's left of the stream). Off: charging waits until the stream ends")] bool chargeWhileFiring = false;
    [SerializeField, Tooltip("Move speed while firing, x walking speed")] float fireMoveSpeed = 0.7f;
    [SerializeField, Tooltip("Seconds between shots while firing")] float fireRate = 0.06f;
    [SerializeField, Tooltip("Each shot's launch speed (metres per second; its Ballistics shape it from there)")] float shotSpeed = 22f;
    [SerializeField, Tooltip("Speed and gravity over each shot's flight")] Ballistics ballistics = new Ballistics();
    [SerializeField, Tooltip("Each shot's splat size (also how big it's drawn)")] int splashSize = 9;
    [SerializeField, Tooltip("Each shot's damage, by how long it's flown")] DamageFalloff damage = new DamageFalloff(28f, DamageFalloff.HoldThenFall(0.2f, 0.5f, 0.5f));
    [SerializeField, Tooltip("Ink per shot, in thousandths of a full tank")] float inkCostPerShot = 9f;
    [SerializeField, Tooltip("Random aim wobble left and right on each shot (degrees, ±)")] float yawSpread = 4f;
    [SerializeField, Tooltip("Random aim wobble up and down on each shot (degrees, ±)")] float pitchSpread = 2f;
    [SerializeField, Tooltip("Degrees above where the gun points that the shots leave: lifts them to reach the cursor (the barrels hang below the hand, and the shots drop)")] float shotPitch = 4f;
    [SerializeField, Tooltip("How the shots are drawn")] ShotLook shotLook = new ShotLook();

    [Header("Fillers")]
    [SerializeField, Tooltip("Shorter, slower shots with each one, spaced between us and it: they ink the floor in between. They do no damage")] int fillerShots = 2;
    [SerializeField, Tooltip("Extra random wobble on each filler shot, up/down and left/right (degrees, ±)")] float fillerSpread = 8f;
    [SerializeField, Range(0f, 1f), Tooltip("Share of the filler shots that are drawn (the rest only paint)")] float fillersSeen = 0.33f;
    [SerializeField, Tooltip("How the (seen) filler shots are drawn")] ShotLook fillerLook = new ShotLook();

    [Header("Barrels")]
    [SerializeField, Tooltip("How fast the barrels spin while firing (degrees per second); they wind up as it charges and spin down after")] float barrelSpin = 1080f;
    [SerializeField, Tooltip("How quickly the barrels reach a new spin speed (full speeds per second)")] float spinChange = 2f;

    float firingLeft, nextShot; // seconds of stream left; until the next shot
    Transform barrels;          // the model's spinning piece (turns on its own up axis: the barrels' length)
    float spin, lastReplay = -1f;

    public float BurstTime => burstTime;
    public bool RequireFullCharge { get => requireFullCharge; set => requireFullCharge = value; } // tests try the variants
    public bool ChargeWhileFiring { get => chargeWhileFiring; set => chargeWhileFiring = value; }
    public float FireMoveSpeed => fireMoveSpeed;
    public float FireRate => fireRate;
    public bool Firing => firingLeft > 0f;
    public int Shots { get; private set; }
    public int Bursts { get; private set; }
    public bool HasBarrels => barrels != null;
    public float Spin => spin; // 0..1 of barrelSpin (tests)

    protected override void Start()
    {
        base.Start();
        foreach (Transform t in GetComponentsInChildren<Transform>(true))
            if (t.name.EndsWith("_Barrels")) barrels = t;
    }

    // The barrels: winding up with the charge, full speed firing; others' while they charge (their
    // WeaponDown) and as their shots come in.
    void LateUpdate()
    {
        PlayerController player = Owner;
        if (barrels == null || player == null) return;
        float target = player.IsLocalPlayer ? (Firing ? 1f : Charging ? Charge * 0.6f : 0f)
                                            : Time.time - lastReplay < 0.2f ? 1f : player.WeaponDown ? 0.5f : 0f;
        spin = Mathf.MoveTowards(spin, target, spinChange * Time.deltaTime);
        barrels.Rotate(Vector3.up, spin * barrelSpin * Time.deltaTime, Space.Self);
    }

    protected override bool CanCharge(PlayerController player) => !Firing || chargeWhileFiring;
    protected override float MoveSpeedNow => Firing ? fireMoveSpeed : 1f;
    protected override bool FeetInking => Firing;
    // The reticle's ring: filling as we charge, then draining as the stream fires off (a charge
    // started mid-stream shows instead).
    public override float ChargeShown => Charging || !Firing ? Charge : Mathf.Clamp01(firingLeft / burstTime);

    protected override void Release(PlayerController player, float charge)
    {
        if (requireFullCharge && charge < 1f) return; // let go early: lost
        if (!Firing) nextShot = 0f;
        firingLeft = Mathf.Max(firingLeft, 0f) + charge * burstTime;
        Bursts++;
    }

    protected override void Tick(PlayerController player, bool held, float dt)
    {
        if (!Firing) return;
        if (player.IsSquid || player.IsDead) { firingLeft = 0f; return; } // swimming stops it
        firingLeft -= dt;
        nextShot -= dt;
        while (nextShot <= 0f && Firing)
        {
            nextShot += fireRate;
            if (!player.ConsumeInk(inkCostPerShot * 0.001f)) { firingLeft = 0f; return; } // dry
            Aim(player);
            Shoot(player);
        }
    }

    void Shoot(PlayerController player)
    {
        Shots++;
        Quaternion aim = muzzle.rotation * Quaternion.Euler(-shotPitch + Random.Range(-pitchSpread, pitchSpread), Random.Range(-yawSpread, yawSpread), 0f);
        Vector3 inherit = Inherit();
        int n = 1 + fillerShots;
        var velocities = new float[n * 3];
        var sizes = new int[n];
        var visible = new bool[n];
        for (int i = 0; i < n; i++)
        {
            bool main = i == 0;
            // The main shot, then fillers at even shares of its speed (so they land between us and it).
            Quaternion way = main ? aim : aim * Quaternion.Euler(Random.Range(-fillerSpread, fillerSpread), Random.Range(-fillerSpread, fillerSpread), 0f);
            float speed = main ? shotSpeed : shotSpeed * i / (fillerShots + 1f) + Random.Range(-0.1f, 0.1f);
            Vector3 launch = way * Vector3.forward * speed;
            velocities[i * 3] = launch.x; velocities[i * 3 + 1] = launch.y; velocities[i * 3 + 2] = launch.z;
            sizes[i] = main ? splashSize : Mathf.RoundToInt(splashSize * 0.7f);
            visible[i] = main || Random.value < fillersSeen;
            Fire(muzzle.position, launch, inherit, sizes[i], visible[i], main, player.Team, player.OwnerId, true);
        }
        NetGameManager.Instance?.BroadcastShots(player, muzzle.position, inherit, velocities, sizes, visible);
    }

    // A shot: the main one hurts; fillers only paint.
    void Fire(Vector3 from, Vector3 launch, Vector3 inherit, int size, bool visible, bool main, int team, ulong ownerId, bool authoritative)
    {
        ProjectileManager.Fire(from, launch, size, team, visible, authoritative, ownerId, 0f,
                               ballistics: ballistics, falloff: authoritative && main ? damage : null, inherit: inherit, source: Source,
                               look: main ? shotLook : fillerLook);
    }

    // Their shots (the main one first, then its fillers), visual-only here.
    public override void Replay(ProjectileSpawnData d)
    {
        if (d.velocities == null || d.splashSizes == null || d.visible == null) return;
        lastReplay = Time.time;
        Vector3 inherit = d.inherit != null ? (Vector3)d.inherit : Vector3.zero;
        int n = Mathf.Min(d.splashSizes.Length, d.visible.Length, d.velocities.Length / 3);
        for (int i = 0; i < n; i++)
            Fire(d.origin, new Vector3(d.velocities[i * 3], d.velocities[i * 3 + 1], d.velocities[i * 3 + 2]), inherit, d.splashSizes[i], d.visible[i], i == 0, d.team, d.shooterSteamId, false);
    }
}

using UnityEngine;

// A weapon that charges while attack is held and acts when it's let go (the Sniper's shot, the
// Minigun's burst). Charging can slow us down. Going into swim form normally loses the charge; a
// weapon can instead keep it for a while (swimHoldTime) as long as attack stays held, carrying on
// charging when we surface. Other players see the charge as the synced WeaponDown. Ours is run by a
// ChargeDriver on the player, since the weapon itself is switched off in swim form.
public abstract class ChargeWeapon : Weapon
{
    [Header("Charge")]
    [SerializeField, Tooltip("Seconds to charge fully, holding attack")] protected float chargeTime = 0.5f;
    [SerializeField, Tooltip("Move speed while charging, x walking speed (1: no slowdown)")] protected float chargeMoveSpeed = 1f;
    [SerializeField, Tooltip("Swimming with attack still held keeps the charge for up to this long (seconds), carrying on when we surface. 0: swimming loses the charge")] protected float swimHoldTime = 0f;
    [SerializeField, Tooltip("Where shots leave from")] protected Transform muzzle;

    protected ControlLayer input;
    protected CharacterController body;
    protected WeaponCameraAim cameraAim;
    float swimSince = -1f;
    ChargeDriver driver;

    public float Charge { get; protected set; }                  // 0..1
    public bool Charging { get; private set; }                   // filling this frame
    public bool KeptInSwim => swimSince >= 0f && Charge > 0f;    // held over in swim form
    public float ChargeTime => chargeTime;
    public float ChargeMoveSpeed => chargeMoveSpeed;
    public float SwimHoldTime => swimHoldTime;
    public bool ScriptedFire { get; set; } // tests: hold the trigger
    public override Transform ShotOrigin => muzzle;
    public string Source => DisplayName;  // "splatted with"

    public override void Equip(PlayerController owner)
    {
        base.Equip(owner);
        enabled = true; // remote copies show the charge (RemoteUpdate)
        if (owner.IsLocalPlayer)
        {
            driver = owner.gameObject.AddComponent<ChargeDriver>();
            driver.weapon = this;
        }
    }

    protected virtual void Start()
    {
        input = new ControlLayer();
        input.Enable();
        if (muzzle == null) muzzle = transform;
        if (Owner != null) body = Owner.GetComponent<CharacterController>();
        cameraAim = GetComponent<WeaponCameraAim>();
    }

    protected override void OnDestroy()
    {
        base.OnDestroy();
        input?.Disable();
        input?.Dispose();
        if (driver != null) Destroy(driver);
        if (Owner != null && Owner.IsLocalPlayer) { Owner.WeaponSpeedMultiplier = 1f; Owner.WeaponDown = false; }
    }

    void Update()
    {
        if (Owner != null && !Owner.IsLocalPlayer) RemoteUpdate(Owner);
    }

    // Ours, every frame (from the ChargeDriver: in swim form too).
    public void Drive()
    {
        PlayerController player = Owner;
        if (player == null || input == null) return;

        bool hardware = !player.IsTestPlayer && player.ScriptedInput == null && !InputGate.Blocked && input.Weapon.Attack.ReadValue<float>() != 0;
        bool held = !InputGate.MatchLocked && !player.IsDead && (ScriptedFire || hardware);
        player.AttackHeld = held;
        float dt = Time.deltaTime;
        Charging = false;
        if (player.IsSquid || player.IsDead || InputGate.MatchLocked)
        {
            // In swim form: the charge is kept (attack held) for a while if this weapon allows it.
            if (Charge > 0f && held && swimHoldTime > 0f && !player.IsDead)
            {
                if (swimSince < 0f) swimSince = Time.time;
                if (Time.time - swimSince > swimHoldTime) Charge = 0f;
            }
            else Charge = 0f;
        }
        else
        {
            swimSince = -1f;
            if (held && CanCharge(player))
            {
                Charge = Mathf.Min(1f, Charge + dt / Mathf.Max(chargeTime, 1e-3f));
                Charging = true;
                Aim(player);
            }
            else if (!held && Charge > 0f)
            {
                float charge = Charge;
                Charge = 0f;
                Aim(player);
                Release(player, charge);
            }
        }
        if (Charge <= 0f) swimSince = -1f;
        player.WeaponDown = Charging || KeptInSwim; // other players see the charge
        UpdateFeetInk(player, FeetInking && !player.IsSquid);
        Tick(player, held, dt);
        float speed = MoveSpeedNow;
        player.WeaponSpeedMultiplier = Charging ? Mathf.Min(chargeMoveSpeed, speed) : speed;
    }

    // Face the view, with the weapon pointed where we look.
    protected void Aim(PlayerController player)
    {
        player.MarkAiming();
        if (cameraAim != null) cameraAim.Apply();
    }

    // What running carries into a shot (not jumping or falling).
    protected Vector3 Inherit()
    {
        Vector3 v = body != null ? body.velocity : Vector3.zero;
        v.y = 0f;
        return v;
    }

    public override float ChargeShown => Charge;

    // Ink dropped at our feet now (every feetShotInterval): while charging, and while firing.
    protected virtual bool FeetInking => Charging;
    // Whether holding attack charges now (a Minigun mid-burst may not).
    protected virtual bool CanCharge(PlayerController player) => true;
    // Let go with `charge` (0..1) built up.
    protected abstract void Release(PlayerController player, float charge);
    // Every frame (ours): anything ongoing, like a burst.
    protected virtual void Tick(PlayerController player, bool held, float dt) { }
    // Move speed now apart from charging (x walking speed): a burst's.
    protected virtual float MoveSpeedNow => 1f;
    // Other players' copies, every frame: their charge shows as player.WeaponDown.
    protected virtual void RemoteUpdate(PlayerController player) { }
}

using System.Collections.Generic;
using UnityEngine;

// Main weapon, sub and special. The main weapon and sub are picked in the loadout menu (and
// remembered); weapons are prefabs, equipped under the weapon mount (remote copies follow the
// owner's choice from their state updates). Subs cost a fixed share of the ink tank: a beacon is placed in front of the player,
// a sprinkler is thrown, a curling bomb slides off along the aim (holding the button first runs its
// fuse down). The special (bubble shield) charges from turf inked and damage dealt,
// refills the tank and makes the player immune to damage for a few seconds; or the Inkstrike,
// called in on a spot picked from the map. Specials charge from turf inked and damage dealt, and
// refill the tank. Lives on remote copies too, for their visuals, subs and strikes.
public class PlayerLoadout : MonoBehaviour
{
    [Header("Main weapons")]
    [SerializeField] Weapon[] weapons;     // prefabs; the first is the default
    [SerializeField] Transform weaponMount;

    [Header("Sub: beacon")]
    [SerializeField] Beacon beaconPrefab;          // its ink cost, health and lifetime are on the prefab
    [SerializeField] int maxBeacons = 3;           // placing another breaks the oldest
    [SerializeField] float placeDistance = 1.5f;   // in front of the player

    [Header("Sub: sprinkler")]
    [SerializeField] Sprinkler sprinklerPrefab;    // as is the sprinkler's
    [SerializeField] float throwSpeed = 11f;       // along the aim, plus an upward lob
    [SerializeField] float throwLift = 4f;

    [Header("Sub: curling bomb")]
    [SerializeField] CurlingBomb curlingBombPrefab; // as is the curling bomb's (and its fuse)
    [SerializeField] Vector3 holdOffset = new Vector3(0.6f, 0.45f, 0.2f); // in hand (at our right side) while its fuse is held down

    [Header("Sub ready light")]
    [SerializeField] Light subReadyLight;          // on the ink tank: lit while there's ink for the sub

    [Header("Special: Inkstrike")]
    [SerializeField] InkStrike inkStrikePrefab;

    [Header("Special: bubble shield")]
    [SerializeField] float specialCost = 1000f;    // points to charge
    [SerializeField] float shieldDuration = 5f;
    [SerializeField] Transform shieldVisual;

    [Header("Special charge")]
    [SerializeField] float pointsPerSquareMetre = 1f;  // turf newly turned to our colour
    [SerializeField] float pointsPerDamage = 1f;       // damage dealt to enemies and their subs
    [SerializeField] float deathSpecialKept = 0.5f;    // share of the charge kept when splatted

    public static PlayerLoadout Local { get; private set; }

    public PlayerController Player => player;
    public IReadOnlyList<Weapon> Weapons => weapons;
    public int WeaponIndex => weaponIndex;
    public Weapon CurrentWeapon => equipped; // the instance in our hands
    public Beacon BeaconPrefab => beaconPrefab;
    public Sprinkler SprinklerPrefab => sprinklerPrefab;
    public CurlingBomb CurlingBombPrefab => curlingBombPrefab;
    public CurlingBomb HeldBomb => held;            // cooking in our hand
    public CurlingBomb LastBomb { get; private set; } // tests
    public SubType Sub => sub;
    public string SubName => sub == SubType.Beacon ? "BEACON" : sub == SubType.Sprinkler ? "SPRINKLER" : "CURLING";
    public SpecialType Special => special;
    public string SpecialName => special == SpecialType.BubbleShield ? "BUBBLE" : "INKSTRIKE";
    public InkStrike InkStrikePrefab => inkStrikePrefab;
    public bool Targeting { get; private set; } // picking an Inkstrike spot on the map
    public float SubInkCost => InkCost(sub);
    public Light SubReadyLight => subReadyLight;
    public float InkCost(SubType type)
    {
        SubDevice prefab = type == SubType.Beacon ? beaconPrefab : type == SubType.Sprinkler ? sprinklerPrefab : (SubDevice)curlingBombPrefab;
        return prefab != null ? prefab.InkCost : 1f;
    }
    public bool CanUseSub => player.InkLevel >= SubInkCost;
    public float SpecialPoints => specialPoints;
    public float SpecialCharge => Mathf.Clamp01(specialPoints / specialCost);
    public bool SpecialReady => specialPoints >= specialCost && !ShieldActive;
    public bool ShieldActive => player.Shielded;
    public float ShieldRemaining => Mathf.Max(0f, shieldUntil - Time.time);
    public float ShieldDuration => shieldDuration;

    PlayerController player;
    ControlLayer input;
    float specialPoints, shieldUntil = -1f;
    int nextSubId, weaponIndex, equippedIndex = -1;
    Weapon equipped;
    SubType sub = SubType.Beacon;
    bool wasDead;
    readonly List<Beacon> ownBeacons = new List<Beacon>(); // oldest first
    Sprinkler ownSprinkler;                                // one at a time
    CurlingBomb held;
    Vector3 shieldScale;
    int shieldTintTeam = -1, shieldTintPair = -1;

    void Awake()
    {
        player = GetComponent<PlayerController>();
        if (shieldVisual != null)
        {
            shieldScale = shieldVisual.localScale;
            shieldVisual.localScale = Vector3.zero;
            shieldVisual.gameObject.SetActive(false);
        }
    }

    public const string WeaponPref = "loadout.weapon", SubPref = "loadout.sub", SpecialPref = "loadout.special";
    SpecialType special = SpecialType.BubbleShield;

    void Start()
    {
        if (!player.IsLocalPlayer || player.IsTestPlayer) { Equip(player.WeaponIndex); return; } // test players: no input, saved loadout or charge
        Local = this;
        SetMainWeapon(PlayerPrefs.GetInt(WeaponPref, 0));
        SetSub((SubType)PlayerPrefs.GetInt(SubPref, (int)SubType.Beacon));
        SetSpecial((SpecialType)PlayerPrefs.GetInt(SpecialPref, (int)SpecialType.BubbleShield));
        input = new ControlLayer();
        input.Weapon.Enable();
        // Real controls only when nothing's scripting the player (tests call these directly).
        input.Weapon.Sub.performed += _ => { if (!InputGate.Blocked && player.ScriptedInput == null) PressSub(); };
        input.Weapon.Sub.canceled += _ => { if (player.ScriptedInput == null) ReleaseSub(); };
        input.Weapon.Special.performed += _ => { if (!InputGate.Blocked && player.ScriptedInput == null) UseSpecial(); };
        SurfaceInkManager.OnTurfInked += OnTurfInked;
    }

    void OnDestroy()
    {
        if (Local == this) Local = null;
        input?.Disable();
        input?.Dispose();
        SurfaceInkManager.OnTurfInked -= OnTurfInked;
    }

    void Update()
    {
        if (player.IsLocalPlayer)
        {
            if (player.IsDead && !wasDead) specialPoints *= deathSpecialKept;
            wasDead = player.IsDead;
            player.Shielded = Time.time < shieldUntil && !player.IsDead;
            player.SpecialCharged = SpecialReady;
            player.SubReady = CanUseSub && !player.IsDead;
            UpdateQueuedSub();
            UpdateHeldBomb();
        }
        else if (player.WeaponIndex != equippedIndex) Equip(player.WeaponIndex); // they switched
        UpdateShieldVisual();
        if (subReadyLight != null && subReadyLight.enabled != player.SubReady) subReadyLight.enabled = player.SubReady;
    }

    // ── Choices (loadout menu) ─────────────────────────────────────────────

    public void SetMainWeapon(int index)
    {
        if (weapons == null || weapons.Length == 0) return;
        weaponIndex = Mathf.Clamp(index, 0, weapons.Length - 1);
        Equip(weaponIndex);
        player.WeaponIndex = weaponIndex; // sent with our state
        if (player.IsLocalPlayer && !player.IsTestPlayer) PlayerPrefs.SetInt(WeaponPref, weaponIndex);
    }

    // Swaps the weapon in our hands for a fresh instance of the chosen prefab.
    void Equip(int index)
    {
        if (weapons == null || weapons.Length == 0 || weaponMount == null) return;
        index = Mathf.Clamp(index, 0, weapons.Length - 1);
        if (index == equippedIndex && equipped != null) return;
        if (equipped != null)
        {
            equipped.gameObject.SetActive(false); // gone from lookups now, destroyed at the end of the frame
            Destroy(equipped.gameObject);
        }
        equipped = Instantiate(weapons[index], weaponMount, false);
        equipped.Equip(player);
        equippedIndex = index;
    }

    public void SetSpecial(SpecialType type)
    {
        special = System.Enum.IsDefined(typeof(SpecialType), type) ? type : SpecialType.BubbleShield;
        if (player.IsLocalPlayer && !player.IsTestPlayer) PlayerPrefs.SetInt(SpecialPref, (int)special);
    }

    public void SetSub(SubType type)
    {
        sub = System.Enum.IsDefined(typeof(SubType), type) ? type : SubType.Beacon;
        if (player.IsLocalPlayer && !player.IsTestPlayer) PlayerPrefs.SetInt(SubPref, (int)sub);
    }

    // ── Sub ────────────────────────────────────────────────────────────────

    bool CanStartSub => player.IsLocalPlayer && !player.IsDead && !player.IsRespawning && !player.IsSuperJumping && player.Team != 0 && !InputGate.MatchLocked && CanUseSub;

    // Pressed (or a held curling bomb let go) while coming out of swim form, before the kid form is
    // up to use ink: it goes as soon as it is (the model snaps up for it), within SubQueueTime.
    const float SubQueueTime = 0.5f;
    float pressQueuedAt = -1f, releaseQueuedAt = -1f;
    public bool SubQueued => pressQueuedAt >= 0f || releaseQueuedAt >= 0f; // tests

    bool InTransition => player.IsLocalPlayer && !player.IsSquid && !player.KidFormReady;

    void UpdateQueuedSub()
    {
        if (player.IsSquid || player.IsDead) pressQueuedAt = releaseQueuedAt = -1f; // back under, or gone: dropped
        if (pressQueuedAt >= 0f && Time.time - pressQueuedAt > SubQueueTime) pressQueuedAt = -1f;
        if (releaseQueuedAt >= 0f && Time.time - releaseQueuedAt > SubQueueTime) releaseQueuedAt = -1f;
        if (player.KidFormReady)
        {
            if (pressQueuedAt >= 0f) { pressQueuedAt = -1f; PressSub(); }
            if (releaseQueuedAt >= 0f) { releaseQueuedAt = -1f; ReleaseSub(); }
        }
        player.ActionQueued = SubQueued;
    }

    // Press and release at once (a curling bomb goes with its full fuse).
    public bool UseSub()
    {
        if (!CanStartSub) return false;
        if (InTransition && sub != SubType.CurlingBomb) { pressQueuedAt = Time.time; player.ActionQueued = true; return true; }
        player.MarkAiming(0.5f); // turn to the view to throw or place it
        if (sub == SubType.CurlingBomb) return PressSub() && ReleaseSub();
        return sub == SubType.Beacon ? PlaceBeacon() : ThrowSprinkler();
    }

    // The sub button went down: most subs go now; a curling bomb is taken in hand, its fuse running.
    public bool PressSub()
    {
        if (sub != SubType.CurlingBomb) return UseSub();
        if (!CanStartSub || held != null || curlingBombPrefab == null) return false;
        player.MarkAiming(0.5f);
        held = Instantiate(curlingBombPrefab, player.transform);
        held.transform.localPosition = holdOffset;
        held.transform.localRotation = Quaternion.identity;
        held.Hold(player.Team);
        return true;
    }

    // The sub button came up: a held curling bomb is thrown with what's left of its fuse.
    public bool ReleaseSub()
    {
        if (held == null) return false;
        if (InTransition) { releaseQueuedAt = Time.time; player.ActionQueued = true; return true; } // thrown once the kid form's up
        CurlingBomb bomb = held;
        held = null;
        if (!CanStartSub || !player.ConsumeInk(InkCost(SubType.CurlingBomb))) { Destroy(bomb.gameObject); return false; }
        Transform aim = player.CameraRig != null ? player.CameraRig : player.transform;
        bomb.transform.SetParent(null, true);
        bomb.transform.rotation = Quaternion.identity;
        bomb.Init(player.OwnerId, nextSubId++, player.Team, ownedLocally: true);
        bomb.Launch(bomb.ThrowVelocity(aim.forward), Mathf.Max(bomb.MinFuse, bomb.FuseLeft));
        LastBomb = bomb;
        NetGameManager.Instance?.SendSubSpawn(bomb);
        return true;
    }

    // Held too long, it goes by itself; dying (or anything else that stops us acting) drops it unused.
    void UpdateHeldBomb()
    {
        if (held == null) return;
        if (player.IsDead || player.IsRespawning || player.IsSuperJumping || InputGate.MatchLocked) { Destroy(held.gameObject); held = null; return; }
        player.MarkAiming(0.5f); // facing the view while it's in hand
        if (held.FuseLeft <= held.MinFuse) ReleaseSub();
    }

    // On the ground in front of the player; needs a spot to stand it on.
    bool PlaceBeacon()
    {
        if (beaconPrefab == null || !FindBeaconSpot(out Vector3 spot) || !player.ConsumeInk(InkCost(SubType.Beacon))) return false;

        ownBeacons.RemoveAll(b => b == null);
        if (ownBeacons.Count >= maxBeacons) { ownBeacons[0].Break(); ownBeacons.RemoveAt(0); }

        Beacon beacon = Instantiate(beaconPrefab, spot, Quaternion.Euler(0f, player.transform.eulerAngles.y, 0f));
        beacon.Init(player.OwnerId, nextSubId++, player.Team, ownedLocally: true);
        ownBeacons.Add(beacon);
        NetGameManager.Instance?.SendSubSpawn(beacon);
        return true;
    }

    // Lobbed along the camera's aim; a new one replaces the last.
    bool ThrowSprinkler()
    {
        if (sprinklerPrefab == null || !player.ConsumeInk(InkCost(SubType.Sprinkler))) return false;
        if (ownSprinkler != null) ownSprinkler.Break();

        Transform aim = player.CameraRig != null ? player.CameraRig : player.transform;
        Vector3 from = player.BodyCenter + Vector3.up * 0.4f + Vector3.ProjectOnPlane(aim.forward, Vector3.up).normalized * 0.6f;
        ownSprinkler = Instantiate(sprinklerPrefab, from, Quaternion.identity);
        ownSprinkler.Init(player.OwnerId, nextSubId++, player.Team, ownedLocally: true);
        ownSprinkler.Throw(aim.forward * throwSpeed + Vector3.up * throwLift);
        NetGameManager.Instance?.SendSubSpawn(ownSprinkler);
        return true;
    }

    // Ground under a point ahead, pulled back if a wall is in the way.
    bool FindBeaconSpot(out Vector3 spot)
    {
        Vector3 from = player.BodyCenter;
        Vector3 forward = Vector3.ProjectOnPlane(player.transform.forward, Vector3.up).normalized;
        float distance = placeDistance;
        if (Physics.Raycast(from, forward, out RaycastHit wall, placeDistance, PhysicsLayers.Environment, QueryTriggerInteraction.Ignore))
            distance = Mathf.Max(0f, wall.distance - 0.4f);
        Vector3 above = from + forward * distance + Vector3.up * 0.5f;
        bool found = Physics.Raycast(above, Vector3.down, out RaycastHit ground, 4f, PhysicsLayers.Environment, QueryTriggerInteraction.Ignore);
        spot = found ? ground.point : Vector3.zero;
        return found;
    }

    // Remote players' subs, from their SubSpawn messages. A sprinkler sends one when thrown and
    // another when it lands; either may arrive first.
    public void SpawnRemoteSub(SubData d)
    {
        SubDevice existing = SubDevice.Find(d.ownerId, d.subId);
        if (existing is Sprinkler landing && d.landed) { landing.Land(d.position, d.normal); return; }
        if (existing is CurlingBomb bounced) { bounced.Resync(d.position, d.velocity, d.fuse); return; }
        if (existing != null) return;

        if ((SubType)d.subType == SubType.CurlingBomb)
        {
            if (curlingBombPrefab == null) return;
            CurlingBomb b = Instantiate(curlingBombPrefab, d.position, Quaternion.identity);
            b.Init(d.ownerId, d.subId, d.team, ownedLocally: false);
            b.Launch(d.velocity, d.fuse);
        }
        else if ((SubType)d.subType == SubType.Sprinkler)
        {
            if (sprinklerPrefab == null) return;
            Sprinkler s = Instantiate(sprinklerPrefab, d.position, Quaternion.identity);
            s.Init(d.ownerId, d.subId, d.team, ownedLocally: false);
            if (d.landed) s.Land(d.position, d.normal); else s.Throw(d.velocity);
        }
        else if (beaconPrefab != null)
        {
            Beacon beacon = Instantiate(beaconPrefab, d.position, Quaternion.identity);
            beacon.Init(d.ownerId, d.subId, d.team, ownedLocally: false);
        }
    }

    // ── Special ────────────────────────────────────────────────────────────

    public bool UseSpecial()
    {
        if (!player.IsLocalPlayer || player.IsDead || player.IsRespawning || player.Team == 0 || !SpecialReady || InputGate.MatchLocked) return false;
        if (special == SpecialType.InkStrike) return BeginInkStrike();
        specialPoints = 0f;
        player.RefillInk();
        shieldUntil = Time.time + shieldDuration;
        player.Shielded = true;
        return true;
    }

    // Opens the map to pick the spot; the charge is only spent once one is picked.
    bool BeginInkStrike()
    {
        MapScreen map = FindFirstObjectByType<MapScreen>();
        if (map == null || inkStrikePrefab == null || Targeting) return false;
        Targeting = map.BeginTargeting(inkStrikePrefab.Radius, LaunchInkStrike, () => Targeting = false);
        return Targeting;
    }

    // The spot picked on the map (tests may call this directly).
    public void LaunchInkStrike(Vector3 target)
    {
        Targeting = false;
        if (!SpecialReady || player.IsDead || InputGate.MatchLocked) return; // splatted, or the match moved on, while picking
        specialPoints = 0f;
        player.RefillInk();
        Instantiate(inkStrikePrefab).Begin(player.OwnerId, player.Team, target, ownedLocally: true);
        NetGameManager.Instance?.SendInkStrike(player.OwnerId, player.Team, target);
    }

    // Other players' strikes, from their InkStrike message: marker and blast only.
    public void SpawnRemoteInkStrike(InkStrikeData d)
    {
        if (inkStrikePrefab != null) Instantiate(inkStrikePrefab).Begin(d.ownerId, d.team, d.position, ownedLocally: false);
    }

    // No charge while the special is running.
    public void AddSpecialPoints(float points)
    {
        if (ShieldActive || points <= 0f) return;
        specialPoints = Mathf.Min(specialCost, specialPoints + points);
    }

    // Match start: no charge, no shield, full tank.
    public void ResetForMatch()
    {
        specialPoints = 0f;
        shieldUntil = -1f;
        player.Shielded = false;
        player.RefillInk();
    }

    public void SetSpecialPoints(float points) => specialPoints = Mathf.Clamp(points, 0f, specialCost); // tests, debug
    public bool ShieldVisible => shieldVisual != null && shieldVisual.gameObject.activeSelf;

    void OnTurfInked(int team, float area)
    {
        if (team == player.Team) AddSpecialPoints(area * pointsPerSquareMetre);
    }

    // Called wherever a hit is accepted; only the local attacker's own hits count.
    public static void ReportDamageDealt(ulong attackerId, int fromTeam, float amount)
    {
        PlayerLoadout local = Local;
        if (local == null || attackerId != local.player.OwnerId || fromTeam != local.player.Team) return;
        local.AddSpecialPoints(amount * local.pointsPerDamage);
    }

    // Pops in and out; shown for remote players from their replicated state.
    void UpdateShieldVisual()
    {
        if (shieldVisual == null) return;
        bool on = player.Shielded && !player.IsDead;
        if (on && (shieldTintTeam != player.Team || shieldTintPair != NetGameManager.Instance?.ColourPair)) TintShield();
        Vector3 target = on ? shieldScale : Vector3.zero;
        shieldVisual.localScale = Vector3.Lerp(shieldVisual.localScale, target, 1f - Mathf.Exp(-14f * Time.deltaTime));
        shieldVisual.gameObject.SetActive(on || shieldVisual.localScale.x > 0.02f);
    }

    void TintShield()
    {
        shieldTintTeam = player.Team;
        NetGameManager gm = NetGameManager.Instance;
        if (gm == null) return;
        shieldTintPair = gm.ColourPair;
        var block = new MaterialPropertyBlock();
        block.SetColor("_Color", player.Team == 2 ? gm.BetaTeam : gm.AlphaTeam);
        foreach (Renderer r in shieldVisual.GetComponentsInChildren<Renderer>(true)) r.SetPropertyBlock(block);
    }
}

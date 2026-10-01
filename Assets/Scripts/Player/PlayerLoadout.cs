using System.Collections.Generic;
using UnityEngine;

// Sub and special weapons. The sub (beacon) costs a fixed share of the ink tank. The special
// (bubble shield) charges from turf inked and damage dealt, refills the tank and makes the player
// immune to damage for a few seconds. Lives on remote copies too, for their visuals and beacons.
public class PlayerLoadout : MonoBehaviour
{
    [Header("Sub: beacon")]
    [SerializeField] Beacon beaconPrefab;
    [SerializeField] float subInkCost = 0.7f;
    [SerializeField] int maxBeacons = 3;           // placing another breaks the oldest
    [SerializeField] float placeDistance = 1.5f;   // in front of the player

    [Header("Special: bubble shield")]
    [SerializeField] float specialCost = 1000f;    // points to charge
    [SerializeField] float shieldDuration = 5f;
    [SerializeField] Transform shieldVisual;

    [Header("Special charge")]
    [SerializeField] float pointsPerSquareMetre = 1f;  // turf newly turned to our colour
    [SerializeField] float pointsPerDamage = 1f;       // damage dealt to enemies and their beacons
    [SerializeField] float deathSpecialKept = 0.5f;    // share of the charge kept when splatted

    public static PlayerLoadout Local { get; private set; }

    public PlayerController Player => player;
    public Beacon BeaconPrefab => beaconPrefab;
    public float SubInkCost => subInkCost;
    public bool CanUseSub => player.InkLevel >= subInkCost;
    public float SpecialPoints => specialPoints;
    public float SpecialCharge => Mathf.Clamp01(specialPoints / specialCost);
    public bool SpecialReady => specialPoints >= specialCost && !ShieldActive;
    public bool ShieldActive => player.Shielded;
    public float ShieldRemaining => Mathf.Max(0f, shieldUntil - Time.time);
    public float ShieldDuration => shieldDuration;

    PlayerController player;
    ControlLayer input;
    float specialPoints, shieldUntil = -1f;
    int nextBeaconId;
    bool wasDead;
    readonly List<Beacon> ownBeacons = new List<Beacon>(); // oldest first
    Vector3 shieldScale;
    int shieldTintTeam = -1;

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

    void Start()
    {
        if (!player.IsLocalPlayer) return;
        Local = this;
        input = new ControlLayer();
        input.Weapon.Enable();
        input.Weapon.Sub.performed += _ => { if (!InputGate.Blocked) UseSub(); };
        input.Weapon.Special.performed += _ => { if (!InputGate.Blocked) UseSpecial(); };
        SurfaceInkManager.OnTurfInked += OnTurfInked;
    }

    void OnDestroy()
    {
        if (Local == this) Local = null;
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
        }
        UpdateShieldVisual();
    }

    // ── Sub ────────────────────────────────────────────────────────────────

    // Places a beacon on the ground in front of the player. Needs the ink and a spot to stand it on.
    public bool UseSub()
    {
        if (!player.IsLocalPlayer || player.IsDead || player.IsSuperJumping || player.Team == 0 || beaconPrefab == null) return false;
        if (!CanUseSub || !FindBeaconSpot(out Vector3 spot)) return false;
        if (!player.ConsumeInk(subInkCost)) return false;

        ownBeacons.RemoveAll(b => b == null);
        if (ownBeacons.Count >= maxBeacons) { ownBeacons[0].Break(); ownBeacons.RemoveAt(0); }

        Beacon beacon = Instantiate(beaconPrefab, spot, Quaternion.Euler(0f, player.transform.eulerAngles.y, 0f));
        beacon.Init(player.OwnerId, nextBeaconId++, player.Team, ownedLocally: true);
        ownBeacons.Add(beacon);
        NetGameManager.Instance?.SendBeaconSpawn(beacon);
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

    // Remote players' beacons, from their BeaconSpawn message.
    public void SpawnRemoteBeacon(BeaconData d)
    {
        if (beaconPrefab == null || Beacon.Find(d.ownerId, d.beaconId) != null) return;
        Beacon beacon = Instantiate(beaconPrefab, d.position, Quaternion.identity);
        beacon.Init(d.ownerId, d.beaconId, d.team, ownedLocally: false);
    }

    // ── Special ────────────────────────────────────────────────────────────

    public bool UseSpecial()
    {
        if (!player.IsLocalPlayer || player.IsDead || player.Team == 0 || !SpecialReady) return false;
        specialPoints = 0f;
        player.RefillInk();
        shieldUntil = Time.time + shieldDuration;
        player.Shielded = true;
        return true;
    }

    // No charge while the special is running.
    public void AddSpecialPoints(float points)
    {
        if (ShieldActive || points <= 0f) return;
        specialPoints = Mathf.Min(specialCost, specialPoints + points);
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
        if (on && shieldTintTeam != player.Team) TintShield();
        Vector3 target = on ? shieldScale : Vector3.zero;
        shieldVisual.localScale = Vector3.Lerp(shieldVisual.localScale, target, 1f - Mathf.Exp(-14f * Time.deltaTime));
        shieldVisual.gameObject.SetActive(on || shieldVisual.localScale.x > 0.02f);
    }

    void TintShield()
    {
        shieldTintTeam = player.Team;
        NetGameManager gm = NetGameManager.Instance;
        if (gm == null) return;
        var block = new MaterialPropertyBlock();
        block.SetColor("_Color", player.Team == 2 ? gm.BetaTeam : gm.AlphaTeam);
        foreach (Renderer r in shieldVisual.GetComponentsInChildren<Renderer>(true)) r.SetPropertyBlock(block);
    }
}

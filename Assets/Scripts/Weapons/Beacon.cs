using System.Collections.Generic;
using UnityEngine;

// Sub weapon: a Super Jump point for its team. Breaks when its health runs out, when its
// lifetime ends, or when someone lands on it. The owner's client is authoritative for its
// health and lifetime; hits on another player's beacon are forwarded to them.
public class Beacon : MonoBehaviour, ISuperJumpTarget
{
    [SerializeField] float maxHealth = 35f;
    [SerializeField] float lifetime = 30f;
    [SerializeField] Renderer[] teamTinted; // parts shown in the team colour
    [SerializeField] Transform spinner;     // turns while active

    static readonly List<Beacon> all = new List<Beacon>();
    public static IReadOnlyList<Beacon> All => all;

    public ulong OwnerId { get; private set; }
    public int Id { get; private set; }
    public int Team { get; private set; }
    public float Health => health;
    public bool IsOwnedLocally => authoritative;

    float health, expiresAt;
    bool authoritative, broken;

    public void Init(ulong ownerId, int id, int team, bool ownedLocally)
    {
        OwnerId = ownerId;
        Id = id;
        Team = team;
        authoritative = ownedLocally;
        health = maxHealth;
        expiresAt = Time.time + lifetime;
        all.Add(this);

        NetGameManager gm = NetGameManager.Instance;
        Color colour = gm == null ? Color.white : team == 2 ? gm.BetaTeam : gm.AlphaTeam;
        var block = new MaterialPropertyBlock();
        block.SetColor("_Color", colour);
        block.SetColor("_EmissionColor", colour * 0.6f);
        foreach (Renderer r in teamTinted) if (r != null) r.SetPropertyBlock(block);
    }

    void OnDestroy() => all.Remove(this);

    void Update()
    {
        if (spinner != null) spinner.Rotate(0f, 120f * Time.deltaTime, 0f, Space.World);
        if (authoritative && Time.time >= expiresAt) Break();
    }

    // Ignores friendly fire. Remote copies forward the hit to the owner.
    public void TakeDamage(float amount, int fromTeam, ulong attackerId)
    {
        if (broken || fromTeam == Team) return;
        PlayerLoadout.ReportDamageDealt(attackerId, fromTeam, amount);
        if (!authoritative)
        {
            NetGameManager.Instance?.SendBeaconDamage(OwnerId, Id, amount, fromTeam, attackerId);
            return;
        }
        health -= amount;
        if (health <= 0f) Break();
    }

    // Removes it here and on every other client. Anyone may break it (landing on it does).
    public void Break()
    {
        if (broken) return;
        NetGameManager.Instance?.SendBeaconDestroy(OwnerId, Id);
        Remove();
    }

    // Local removal only, for network messages and resets.
    public void Remove()
    {
        broken = true;
        all.Remove(this);
        Destroy(gameObject);
    }

    public void ExpireIn(float seconds) => expiresAt = Time.time + seconds; // tests

    // ── ISuperJumpTarget ───────────────────────────────────────────────────
    public Vector3 JumpPosition => transform.position;
    public bool IsValidTargetFor(PlayerController jumper) => !broken && jumper != null && jumper.Team == Team;
    public void OnSuperJumpLanded(PlayerController jumper) => Break();

    // ── Registry ───────────────────────────────────────────────────────────
    public static Beacon Find(ulong ownerId, int id)
    {
        foreach (Beacon b in all)
            if (b != null && b.OwnerId == ownerId && b.Id == id) return b;
        return null;
    }

    public static void RemoveOwnedBy(ulong ownerId)
    {
        for (int i = all.Count - 1; i >= 0; i--)
            if (all[i] != null && all[i].OwnerId == ownerId) all[i].Remove();
    }

    public static void RemoveAll()
    {
        for (int i = all.Count - 1; i >= 0; i--)
            if (all[i] != null) all[i].Remove();
        all.Clear();
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() => all.Clear();
}

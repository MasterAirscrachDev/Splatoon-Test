using System.Collections.Generic;
using UnityEngine;

// A sub weapon out in the level (beacon, sprinkler). Breaks when its health runs out or its
// lifetime ends. The owner's client is authoritative for both; hits on another player's sub are
// forwarded to them. Anyone may break one (landing on a beacon does), so breaks are broadcast.
public abstract class SubDevice : MonoBehaviour
{
    [SerializeField] protected float inkCost = 0.6f;  // share of the ink tank it takes to use
    [SerializeField] protected float maxHealth = 35f;
    [SerializeField] protected float lifetime = 30f;
    [SerializeField] protected Renderer[] teamTinted; // parts shown in the team colour

    static readonly List<SubDevice> all = new List<SubDevice>();
    public static IReadOnlyList<SubDevice> All => all;

    public abstract SubType Type { get; }
    public ulong OwnerId { get; private set; }
    public int Id { get; private set; }
    public int Team { get; private set; }
    public float Health => health;
    public float MaxHealth => maxHealth;
    public float InkCost => inkCost;
    public float Lifetime => lifetime;
    public float TimeLeft => Mathf.Max(0f, expiresAt - Time.time);
    public bool IsOwnedLocally => authoritative;

    protected float expiresAt;
    protected bool authoritative, broken;
    float health;

    public virtual void Init(ulong ownerId, int id, int team, bool ownedLocally)
    {
        OwnerId = ownerId;
        Id = id;
        Team = team;
        authoritative = ownedLocally;
        health = maxHealth;
        expiresAt = Time.time + lifetime;
        all.Add(this);
        TintParts(team);
    }

    // The team-coloured parts.
    protected void TintParts(int team)
    {
        NetGameManager gm = NetGameManager.Instance;
        Color colour = gm == null ? Color.white : team == 2 ? gm.BetaTeam : gm.AlphaTeam;
        var block = new MaterialPropertyBlock();
        block.SetColor("_Color", colour);
        block.SetColor("_EmissionColor", colour * 0.6f);
        foreach (Renderer r in teamTinted) if (r != null) r.SetPropertyBlock(block);
    }

    protected virtual void OnDestroy() => all.Remove(this);

    protected virtual void Update()
    {
        if (authoritative && Time.time >= expiresAt) Break();
    }

    // Ignores friendly fire. Remote copies forward the hit to the owner.
    public void TakeDamage(float amount, int fromTeam, ulong attackerId)
    {
        if (broken || fromTeam == Team) return;
        PlayerLoadout.ReportDamageDealt(attackerId, fromTeam, amount);
        if (!authoritative)
        {
            NetGameManager.Instance?.SendSubDamage(OwnerId, Id, amount, fromTeam, attackerId);
            return;
        }
        health -= amount;
        if (health <= 0f) Break();
    }

    // Removes it here and on every other client.
    public void Break()
    {
        if (broken) return;
        NetGameManager.Instance?.SendSubDestroy(OwnerId, Id);
        Remove();
    }

    // Local removal only, for network messages and resets.
    public void Remove()
    {
        broken = true;
        all.Remove(this);
        Destroy(gameObject);
    }

    // Gone as far as the game's concerned (out of the registry) but kept for `delay` seconds to finish an effect.
    protected void Retire(float delay)
    {
        broken = true;
        all.Remove(this);
        Destroy(gameObject, delay);
    }

    public void ExpireIn(float seconds) => expiresAt = Time.time + seconds; // tests

    // ── Registry ───────────────────────────────────────────────────────────
    public static SubDevice Find(ulong ownerId, int id)
    {
        foreach (SubDevice d in all)
            if (d != null && d.OwnerId == ownerId && d.Id == id) return d;
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

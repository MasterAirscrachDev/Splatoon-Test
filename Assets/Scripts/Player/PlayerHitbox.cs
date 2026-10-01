using UnityEngine;

// Player hitbox: a trigger capsule that shrinks in squid form. Only the owning client tracks
// health; hits on a remote copy are forwarded to that player's client.
[RequireComponent(typeof(CapsuleCollider))]
public class PlayerHitbox : MonoBehaviour
{
    [SerializeField] PlayerController player;
    [SerializeField] float maxHealth = 100f;

    [Header("Healing")]
    [SerializeField] float healDelay = 0.5f;                       // seconds after any damage before healing starts
    [SerializeField] float healPerSecond = 100f / 6f;              // full heal in 6s
    [SerializeField] float healPerSecondSubmerged = 100f / 1.5f;   // full heal in 1.5s while swimming in own ink

    [Header("Stand shape")]
    [SerializeField] float standHeight = 1.92f;
    [SerializeField] float standRadius = 0.5f;
    [SerializeField] Vector3 standCenter = Vector3.zero;

    [Header("Swim shape")]
    [SerializeField] float swimHeight = 0.3f;
    [SerializeField] float swimRadius = 0.25f;
    [SerializeField] Vector3 swimCenter = new Vector3(0f, -0.8f, 0f);

    float health;
    float lastDamageTime = -999f;
    CapsuleCollider capsule;

    public float Health => health;
    public float HealthNormalized => maxHealth > 0f ? health / maxHealth : 0f;
    public int Team => player != null ? player.Team : 0;

    void Awake()
    {
        capsule = GetComponent<CapsuleCollider>();
        capsule.isTrigger = true;
        if (player == null) player = GetComponentInParent<PlayerController>();
        health = maxHealth;
    }

    void Update()
    {
        bool squid = player != null && player.IsSquid;
        capsule.height = squid ? swimHeight : standHeight;
        capsule.radius = squid ? swimRadius : standRadius;
        capsule.center = squid ? swimCenter : standCenter;
        capsule.enabled = player == null || !player.IsDead; // IsDead is replicated, so this covers remotes too
        UpdateHealing();
    }

    // Owner only: regenerate after healDelay, faster while submerged in own ink.
    void UpdateHealing()
    {
        if (player == null || !player.IsLocalPlayer || player.IsDead) return;
        if (health >= maxHealth || Time.time - lastDamageTime < healDelay) return;
        float rate = player.IsInInk ? healPerSecondSubmerged : healPerSecond;
        Heal(rate * Time.deltaTime);
    }

    // Returns true if the hit was lethal. Ignores friendly fire, hits while dead, and hits while
    // the bubble shield is up (replicated, so a shooter skips shielded remote copies too).
    public bool TakeDamage(float amount, int fromTeam, ulong attackerId = 0)
    {
        if (player != null && (fromTeam == player.Team || player.IsDead || player.Shielded)) return false;
        PlayerLoadout.ReportDamageDealt(attackerId, fromTeam, amount);
        if (player != null && !player.IsLocalPlayer)
        {
            NetGameManager.Instance?.SendDamage(player.OwnerId, amount, fromTeam, attackerId);
            return false;
        }
        if (health <= 0f) return false;

        lastDamageTime = Time.time;
        health -= amount;
        if (health <= 0f)
        {
            health = 0f;
            Die();
            return true;
        }
        return false;
    }

    public void Heal(float amount)  => health = Mathf.Min(maxHealth, health + amount);
    public void ResetHealth()       => health = maxHealth;

    void Die()
    {
        ResetHealth();
        if (player != null) player.OnDeath();
    }
}

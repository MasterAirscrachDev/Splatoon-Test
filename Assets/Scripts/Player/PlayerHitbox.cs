using UnityEngine;

// Player hitbox: a trigger capsule that shrinks in squid form. Health and death are local for now.
[RequireComponent(typeof(CapsuleCollider))]
public class PlayerHitbox : MonoBehaviour
{
    [SerializeField] PlayerController player;
    [SerializeField] float maxHealth = 100f;

    [Header("Stand shape")]
    [SerializeField] float standHeight = 1.92f;
    [SerializeField] float standRadius = 0.5f;
    [SerializeField] Vector3 standCenter = Vector3.zero;

    [Header("Swim shape")]
    [SerializeField] float swimHeight = 0.3f;
    [SerializeField] float swimRadius = 0.25f;
    [SerializeField] Vector3 swimCenter = new Vector3(0f, -0.8f, 0f);

    float health;
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
    }

    // Returns true if the hit was lethal. Ignores friendly fire and hits while dead.
    public bool TakeDamage(float amount, int fromTeam)
    {
        if (health <= 0f) return false;
        if (player != null && (fromTeam == player.Team || player.IsDead)) return false;

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

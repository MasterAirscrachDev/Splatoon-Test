using UnityEngine;

// Pooled ink projectile (see ProjectilePool): damages enemy hitboxes, splats ink surfaces.
// Remote replays (authoritative = false) only show the flight and impact.
public class ProjectileSystem : MonoBehaviour
{
    int splashSize = 10, team = 1;
    [SerializeField] float damage = 30f;
    [SerializeField] GameObject splashParticlesPrefab;

    Rigidbody             rb;
    Renderer              visual;
    MaterialPropertyBlock propBlock;
    Color                 inkColor = Color.white;
    bool  hasImpacted;     // removal is deferred, so guard against impacting twice meanwhile
    float releaseAt = -1f; // when the deferred return to the pool happens
    bool  authoritative;   // false = remote replay: visuals only, no splat or damage
    ulong ownerId;         // shooter's Steam id
    float shotDamage;      // this shot's damage (the prefab's unless overridden)
    bool  impactParticles; // splash effect on hitting a surface

    static readonly int ShaderDirection = Shader.PropertyToID("_Direction");
    static readonly int ShaderColor     = Shader.PropertyToID("_Color");
    static readonly int ShaderSeed      = Shader.PropertyToID("_Seed");

    void Awake()
    {
        rb = GetComponent<Rigidbody>();
        foreach (Transform child in transform) // visual is the first child renderer
        {
            Renderer r = child.GetComponent<Renderer>();
            if (r != null) { visual = r; break; }
        }
        propBlock = new MaterialPropertyBlock();
    }

    // Resets all per-shot state (instances are reused).
    public void Setup(Vector3 velocity, int splashSize, int team, bool visible = true, bool authoritative = true, ulong ownerId = 0,
                      float damage = -1f, bool impactParticles = true)
    {
        shotDamage = damage >= 0f ? damage : this.damage;
        this.impactParticles = impactParticles;
        hasImpacted = false;
        releaseAt = -1f;
        this.authoritative = authoritative;
        this.ownerId = ownerId;
        this.splashSize = splashSize;
        this.team = team;
        rb.linearVelocity = velocity;
        rb.angularVelocity = Vector3.zero;
        NetGameManager gm = NetGameManager.Instance;
        if (gm != null)
            inkColor = (team == 1) ? gm.AlphaTeam : gm.BetaTeam;

        if (visual != null)
        {
            visual.gameObject.SetActive(visible); // hidden filler shots keep the visual for reuse
            if (visible) ApplyVisualColor();
        }
    }

    void ApplyVisualColor()
    {
        if (visual == null) return;
        visual.GetPropertyBlock(propBlock);
        propBlock.SetColor(ShaderColor, inkColor);
        propBlock.SetFloat(ShaderSeed, Random.Range(0f, 100f));
        visual.SetPropertyBlock(propBlock);
        visual.transform.localScale = Vector3.one * (splashSize * 0.035f);
    }

    void Update()
    {
        if (releaseAt >= 0f && Time.time >= releaseAt) { ProjectilePool.Release(this); return; }
        UpdateVisualDirection();
        if (transform.position.y < -10) ProjectilePool.Release(this);
    }

    void UpdateVisualDirection()
    {
        if (visual == null || rb == null) return;

        Vector3 vel     = rb.linearVelocity;
        float   speed   = vel.magnitude;
        Vector3 dir     = speed > 0.1f ? vel / speed : Vector3.down;
        float   stretch = 1f + speed * 0.12f;

        visual.GetPropertyBlock(propBlock);
        propBlock.SetVector(ShaderDirection, new Vector4(dir.x, dir.y, dir.z, stretch));
        visual.SetPropertyBlock(propBlock);
    }

    void OnTriggerEnter(Collider other)
    {
        if (other.CompareTag("Projectile")) return;
        if (hasImpacted) return;

        // Enemy subs and hitboxes take the hit; teammates' (and the shooter) are passed through.
        SubDevice device = other.GetComponentInParent<SubDevice>();
        if (device != null)
        {
            if (device.Team != team)
            {
                hasImpacted = true;
                if (authoritative) device.TakeDamage(shotDamage, team, ownerId);
                DeleteProjectile();
            }
            return;
        }

        PlayerHitbox hitbox = other.GetComponentInParent<PlayerHitbox>();
        if (hitbox != null)
        {
            if (hitbox.Team != team)
            {
                hasImpacted = true;
                if (authoritative) hitbox.TakeDamage(shotDamage, team, ownerId);
                DeleteProjectile();
            }
            return;
        }

        SurfaceInkManager inkManager = other.GetComponent<SurfaceInkManager>();
        if (inkManager != null)
        {
            Vector3 vel = rb != null ? rb.linearVelocity : Vector3.zero;
            Vector3 dir = vel.sqrMagnitude > 0.01f ? vel.normalized : Vector3.down;

            // Cast against this exact collider, starting behind us in case we've already penetrated it.
            if (other.Raycast(new Ray(transform.position - dir * 0.5f, dir), out RaycastHit hit, 2f))
            {
                hasImpacted = true;
                if (authoritative) inkManager.Splat(inkManager.UVFromHit(hit), splashSize, team); // replays get the shooter's Splat message
                if (impactParticles) InkParticles.Spawn(splashParticlesPrefab, hit.point, Quaternion.FromToRotation(Vector3.up, hit.normal), inkColor);
            }
        }

        DeleteProjectile();
    }

    // Returns to the pool shortly, so other trigger callbacks this physics step still complete.
    public void DeleteProjectile()
    {
        if (releaseAt < 0f) releaseAt = Time.time + 0.05f;
    }
}

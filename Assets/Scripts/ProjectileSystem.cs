using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class ProjectileSystem : MonoBehaviour
{
    int splashSize = 10, team = 1;
    bool canRespawn = true;
    [SerializeField] float damage = 30f;

    Rigidbody         rb;
    Renderer          visual;
    MaterialPropertyBlock propBlock;
    Color             inkColor = Color.white;

    static readonly int ShaderDirection = Shader.PropertyToID("_Direction");
    static readonly int ShaderColor     = Shader.PropertyToID("_Color");
    static readonly int ShaderSeed      = Shader.PropertyToID("_Seed");

    void Awake()
    {
        rb = GetComponent<Rigidbody>();

        // Find the first renderer on a child — skip the root (it has the collider, not the visual)
        foreach (Transform child in transform)
        {
            Renderer r = child.GetComponent<Renderer>();
            if (r != null) { visual = r; break; }
        }
        propBlock = new MaterialPropertyBlock();
    }

    void Start()
    {
        // Setup() already ran (called synchronously after Instantiate before Start fires),
        // so `team` is already correct here.
        NetGameManager gm = FindFirstObjectByType<NetGameManager>();
        if (gm != null)
            inkColor = (team == 1) ? gm.AlphaTeam : gm.BetaTeam;

        ApplyVisualColor();
    }

    void ApplyVisualColor()
    {
        if (visual == null) return;
        visual.GetPropertyBlock(propBlock);
        propBlock.SetColor(ShaderColor, inkColor);
        propBlock.SetFloat(ShaderSeed, Random.Range(0f, 100f));
        visual.SetPropertyBlock(propBlock);
        visual.transform.localScale = Vector3.one * (splashSize * 0.035f); // scale the visual to match the splash size
    }

    public void Setup(Vector3 velocity, float force, int splashSize, int team, bool canRespawn = true)
    {
        this.splashSize = splashSize;
        this.team = team;
        this.canRespawn = canRespawn;
        rb.linearVelocity = velocity;
        rb.AddForce(transform.forward * force);
    }

    void Update()
    {
        UpdateVisualDirection();

        if (canRespawn && Random.Range(0, 100) < 1)
        {
            GameObject drip = Instantiate(gameObject, transform.position, Quaternion.identity);
            //flat random directional vector
            Vector3 flatDir = new Vector3(Random.Range(-1f, 1f), 0, Random.Range(-1f, 1f)).normalized * 0.5f;
            drip.GetComponent<ProjectileSystem>().Setup(flatDir, 0, Mathf.RoundToInt(splashSize * 0.8f), team, false);
        }
        if (transform.position.y < -10)
        {
            Destroy(gameObject);
        }
    }

    void UpdateVisualDirection()
    {
        if (visual == null || rb == null) return;

        Vector3 vel   = rb.linearVelocity;
        float   speed = vel.magnitude;
        Vector3 dir   = speed > 0.1f ? vel / speed : Vector3.down;
        float   stretch = 1f + speed * 0.12f; // grows with speed; tune the 0.06 multiplier

        visual.GetPropertyBlock(propBlock);
        propBlock.SetVector(ShaderDirection, new Vector4(dir.x, dir.y, dir.z, stretch));
        visual.SetPropertyBlock(propBlock);
    }

    void OnTriggerEnter(Collider other)
    {
        if (other.CompareTag("Projectile")) return;

        // Player hits take priority over surface splats. Teammates (and the shooter,
        // who overlaps this trigger on spawn) are passed through: no damage, no destroy,
        // so the projectile flies on to hit the wall behind them.
        PlayerHitbox hitbox = other.GetComponentInParent<PlayerHitbox>();
        if (hitbox != null)
        {
            if (hitbox.Team != team)
            {
                hitbox.TakeDamage(damage, team);
                DeleteProjectile();
            }
            return;
        }

        SurfaceInkManager inkManager = other.GetComponent<SurfaceInkManager>();
        if (inkManager != null)
        {
            Vector3 vel = rb != null ? rb.linearVelocity : Vector3.zero;
            Vector3 dir = vel.sqrMagnitude > 0.01f ? vel.normalized : Vector3.down;

            // Cast against `other` directly — it's the exact collider that fired OnTriggerEnter,
            // bypassing any base/physics colliders that Physics.Raycast would hit first.
            // Offset 0.5 + distance 2 handles fast projectiles that have penetrated the surface.
            RaycastHit hit;
            if (other.Raycast(new Ray(transform.position - dir * 0.5f, dir), out hit, 2f))
            {
                // textureCoord is only populated by non-convex MeshColliders.
                // Walls often use BoxColliders or convex meshes, giving (0,0).
                // Fall back to deriving UV from world position via the renderer's local bounds.
                Vector2 uv = hit.textureCoord;
                if (uv.sqrMagnitude < 0.0001f)
                    uv = FallbackUV(hit, inkManager);
                //Debug.Log($"Projectile hit {other.name} at {hit.point} with UV {uv}");
                inkManager.Splat(uv, splashSize, team);
            }
        }

        // Always destroy on any non-projectile contact. Unity defers Destroy to end-of-frame
        // so all OnTriggerEnter callbacks in the same physics step still complete.
        DeleteProjectile();
    }

    // Derives a 0-1 UV from the hit world position when textureCoord is unavailable.
    // Projects the hit point into local space and normalises against the renderer's bounds.
    // Works for any flat surface regardless of orientation (floor, wall, ceiling).
    static Vector2 FallbackUV(RaycastHit hit, SurfaceInkManager inkManager)
    {
        Renderer rend = inkManager.GetComponent<Renderer>();
        if (rend == null) return Vector2.zero;

        Bounds b = rend.localBounds;
        if (b.size.sqrMagnitude < 0.0001f) return Vector2.zero;

        Vector3 p = inkManager.transform.InverseTransformPoint(hit.point);

        // The surface normal (in local space) identifies the depth axis.
        // The two remaining axes become U and V.
        Vector3 n = inkManager.transform.InverseTransformDirection(hit.normal);
        float ax = Mathf.Abs(n.x), ay = Mathf.Abs(n.y), az = Mathf.Abs(n.z);

        float u, v;
        if (ay >= ax && ay >= az)       // floor / ceiling — normal is mostly Y
        {
            u = Mathf.InverseLerp(b.min.x, b.max.x, p.x);
            v = Mathf.InverseLerp(b.min.z, b.max.z, p.z);
        }
        else if (az >= ax)              // wall facing ±Z — normal is mostly Z
        {
            u = Mathf.InverseLerp(b.min.x, b.max.x, p.x);
            v = Mathf.InverseLerp(b.min.y, b.max.y, p.y);
        }
        else                            // wall facing ±X — normal is mostly X
        {
            u = Mathf.InverseLerp(b.min.z, b.max.z, p.z);
            v = Mathf.InverseLerp(b.min.y, b.max.y, p.y);
        }
        return new Vector2(u, v);
    }

    public void DeleteProjectile()
    {
        Destroy(gameObject, 0.05f);
    }
}

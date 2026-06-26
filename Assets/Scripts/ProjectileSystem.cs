using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class ProjectileSystem : MonoBehaviour
{
    int splashSize = 10, team = 1;
    bool canRespawn = true;

    public void Setup(Vector3 velocity, float force, int splashSize, int team, bool canRespawn = true)
    {
        this.splashSize = splashSize;
        this.team = team;
        this.canRespawn = canRespawn;
        GetComponent<Rigidbody>().linearVelocity = velocity;
        GetComponent<Rigidbody>().AddForce(transform.forward * force);
    }

    void Update()
    {
        if (canRespawn && Random.Range(0, 100) < 1)
        {
            GameObject drip = Instantiate(gameObject, transform.position, Quaternion.identity);
            drip.GetComponent<ProjectileSystem>().Setup(Vector3.zero, 0, splashSize, team, false);
        }
        if (transform.position.y < -10)
        {
            Destroy(gameObject);
        }
    }

    void OnTriggerEnter(Collider other)
    {
        if (other.CompareTag("Projectile")) return;

        SurfaceInkManager inkManager = other.GetComponent<SurfaceInkManager>();
        if (inkManager != null)
        {
            Rigidbody rb = GetComponent<Rigidbody>();
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

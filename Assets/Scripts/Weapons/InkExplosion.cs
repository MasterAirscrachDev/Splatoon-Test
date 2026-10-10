using System.Collections.Generic;
using UnityEngine;

// An explosion's tuning: its reach, the damage in its core falling to the edge, and the ink it
// throws on everything around it.
[System.Serializable]
public class InkBlast
{
    public float radius = 2.4f;
    public float coreRadius = 1f;       // full damage within this
    public float coreDamage = 70f;
    public float edgeDamage = 35f;      // at the very edge
    public float inkReach = 1.2f;       // it splats surfaces within this directly...
    public int splashSize = 10;         // each splat it leaves
    public int fallingDrops = 5;        // ...and its ink falls to whatever is below it (unseen, harmless)
    public float fallSpread = 0.7f;     // landing up to this far around under it
    public GameObject effect;           // InkBlastEffect, swelling to the radius in the team colour
}

// Explodes ink at a point: everyone sees it (the effect); the owner's copy also paints every
// surface within its ink reach (rays out in all directions, so floors, walls and ceilings), drops
// ink that falls to the surface below (so a blast in mid-air still inks the ground under it), and
// hurts enemies, and their subs, it can see from the centre: full damage in the core, falling to
// the edge damage at the rim.
public static class InkExplosion
{
    static readonly Vector3[] Rays = BuildRays();
    static readonly Collider[] overlaps = new Collider[32];

    static Vector3[] BuildRays()
    {
        var rays = new List<Vector3>();
        for (int x = -1; x <= 1; x++)
            for (int y = -1; y <= 1; y++)
                for (int z = -1; z <= 1; z++)
                    if (x != 0 || y != 0 || z != 0) rays.Add(new Vector3(x, y, z).normalized);
        return rays.ToArray();
    }

    // exclude: whoever it already hit directly (they took the direct damage instead).
    // drop: the projectile its falling ink flies as (none: it doesn't fall).
    public static void Detonate(Vector3 centre, InkBlast blast, int team, ulong ownerId, string source, bool authoritative, Color colour,
                                Object exclude = null, GameObject drop = null)
    {
        if (blast == null) return;
        if (blast.effect != null) InkBlastEffect.Spawn(blast.effect, centre, blast.radius, colour);
        if (!authoritative) return;

        foreach (Vector3 dir in Rays)
        {
            if (!Physics.Raycast(centre, dir, out RaycastHit hit, blast.inkReach, PhysicsLayers.Environment, QueryTriggerInteraction.Ignore)) continue;
            SurfaceInkManager ink = hit.collider.GetComponent<SurfaceInkManager>();
            if (ink != null) ink.SplatAt(hit, blast.splashSize, team);
        }
        if (drop != null) Fall(centre, blast, team, ownerId, drop);
        if (blast.coreDamage <= 0f && blast.edgeDamage <= 0f) return; // ink only (a splatted player's burst)

        var struck = new HashSet<Object>();
        if (exclude != null) struck.Add(exclude);
        int n = Physics.OverlapSphereNonAlloc(centre, blast.radius, overlaps, LayerMask.GetMask("Hitbox"), QueryTriggerInteraction.Collide);
        for (int i = 0; i < n; i++)
        {
            Collider c = overlaps[i];
            Vector3 nearest = c.ClosestPoint(centre);
            if (Physics.Linecast(centre, nearest, PhysicsLayers.Environment, QueryTriggerInteraction.Ignore)) continue; // behind cover
            float damage = DamageAt(blast, Vector3.Distance(centre, nearest));
            PlayerHitbox hitbox = c.GetComponentInParent<PlayerHitbox>();
            if (hitbox != null)
            {
                if (hitbox.Team != team && struck.Add(hitbox)) hitbox.TakeDamage(damage, team, ownerId, source);
                continue;
            }
            SubDevice sub = c.GetComponentInParent<SubDevice>();
            if (sub != null && sub.Team != team && struck.Add(sub)) sub.TakeDamage(damage, team, ownerId);
        }
    }

    // One drop straight down and the rest in a ring around it, falling fast from beside the centre
    // (any that would start inside a wall are skipped).
    static void Fall(Vector3 centre, InkBlast blast, int team, ulong ownerId, GameObject drop)
    {
        float turn = Random.Range(0f, 360f);
        for (int i = 0; i < blast.fallingDrops; i++)
        {
            Vector3 offset = i == 0 ? Vector3.zero
                : Quaternion.Euler(0f, turn + 360f * i / (blast.fallingDrops - 1), 0f) * Vector3.forward * (blast.fallSpread * Random.Range(0.6f, 1f));
            Vector3 from = centre + offset;
            if (i > 0 && Physics.Linecast(centre, from, PhysicsLayers.Environment, QueryTriggerInteraction.Ignore)) continue;
            ProjectileManager.Fire(drop, from, Vector3.down * ProjectileManager.FallSpeed, blast.splashSize, team,
                                   visible: false, authoritative: true, ownerId: ownerId, damage: 0f, impactParticles: false);
        }
    }

    public static float DamageAt(InkBlast blast, float distance)
    {
        if (distance <= blast.coreRadius) return blast.coreDamage;
        return Mathf.Lerp(blast.coreDamage, blast.edgeDamage, Mathf.InverseLerp(blast.coreRadius, blast.radius, distance));
    }
}

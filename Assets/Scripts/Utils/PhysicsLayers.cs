using UnityEngine;

// Shared layer masks for physics queries.
public static class PhysicsLayers
{
    static int environment;
    static bool environmentResolved;

    // Level geometry only (no player bodies, hitboxes or projectiles). Pair with
    // QueryTriggerInteraction.Ignore, since the project has Queries Hit Triggers on.
    public static int Environment
    {
        get
        {
            if (!environmentResolved)
            {
                environment = ~LayerMask.GetMask("Player", "PlayerSwim", "Hitbox", "Projectile", "Ignore Raycast");
                environmentResolved = true;
            }
            return environment;
        }
    }
}

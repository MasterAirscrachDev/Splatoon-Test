using UnityEngine;

// Detects a climbable wall via a ring of short horizontal raycasts, evaluated fresh every
// frame from the player's current position — no persistent trigger/collider state at all.
// A trigger's Enter/Exit events fire for whichever collider bounds overlap a (necessarily
// generous) sphere, so it could keep reporting contact with a wall the player had already
// moved past — e.g. going around a corner. Recasting fresh every frame means the detected
// wall (and its normal) always reflects exactly where the player is right now: corners are
// picked up immediately, there's no need to force the player/camera to face the wall (both
// movement and visuals just read whatever's current), and a wall stops being "climbable" the
// instant it's no longer within short range, not whenever a trigger happens to fire Exit.
public class WallClimbSensor : MonoBehaviour
{
    [SerializeField] int rayCount = 12;
    [SerializeField] float probeDistance = 0.75f; // short — only counts walls immediately adjacent
    // Extra margin above the player's walkable slope limit (PlayerController.SlopeLimit — a
    // fixed value, not the controller's live slopeLimit, which is raised to 90° while climbing)
    // before a surface counts as climbable, so this sensor can't disagree with what the
    // controller treats as a walkable floor/slope.
    [SerializeField] float climbMarginDeg = 5f;
    [SerializeField] LayerMask raycastMask = ~0;
    // How strongly a wall the player is moving into is preferred over a merely closer one,
    // in metres of distance per unit of (intent · -normal). Without it, at an inner corner the
    // wall being climbed is always slightly closer than the one being strafed into, so the
    // player stayed pinned in the corner instead of transferring onto the new wall.
    [SerializeField] float intentBias = 0.5f;
    // How far below the ring to probe for "the wall continues below us" (see WallContinuesBelow).
    [SerializeField] float topProbeDrop = 0.25f;
    [SerializeField] bool debugDraw = true;

    PlayerController player;

    void Awake()
    {
        player = GetComponentInParent<PlayerController>();
    }

    void Update()
    {
        if (player == null) return;

        if (TryFindWall(out RaycastHit hit, out SurfaceInkManager ink, out Vector2 uv, out bool sawNonOwnWall))
            player.SetClimbContact(hit.normal, ink, uv);
        else
            // No own-ink wall, but a climbable wall *is* right there: we've reached the edge of
            // our ink on it, not the edge of the wall itself. Or the wall we were on carries on
            // just below us: we've climbed past its top lip. Both are definite, so they skip the
            // grace window meant for brief misses rounding corners.
            player.ClearClimbContact(atInkEdge: sawNonOwnWall, atWallTop: !sawNonOwnWall && WallContinuesBelow());
    }

    // Casts rayCount evenly-spaced horizontal rays (world-space, independent of the player's
    // facing) outward from this transform, keeping the best hit (closest, biased toward the
    // direction the player is moving — see intentBias) that's steep enough to
    // count as a wall (not a floor/ceiling/shallow slope) AND belongs to the player's own
    // team's ink. A wall of any other team, or unpainted, is not a valid climb target at all
    // — filtered out here at acquisition rather than downstream, so isClimbing being true
    // always means "there is a climbable wall of my own ink right here."
    bool TryFindWall(out RaycastHit bestHit, out SurfaceInkManager bestInk, out Vector2 bestUV, out bool sawNonOwnWall)
    {
        sawNonOwnWall = false;
        bestHit = default;
        bestInk = null;
        bestUV = Vector2.zero;

        float climbThreshold = Mathf.Cos((player.SlopeLimit + climbMarginDeg) * Mathf.Deg2Rad);
        Vector3 origin = transform.position;
        bool found = false;
        float bestScore = float.MaxValue;
        Vector3 intent = player.ClimbIntent;

        for (int i = 0; i < rayCount; i++)
        {
            float angle = i * (360f / rayCount) * Mathf.Deg2Rad;
            Vector3 dir = new Vector3(Mathf.Sin(angle), 0f, Mathf.Cos(angle));

            if (!Physics.Raycast(origin, dir, out RaycastHit hit, probeDistance, raycastMask))
            {
                // Gray: nothing within probe range in this direction
                if (debugDraw) Debug.DrawRay(origin, dir * probeDistance, new Color(0.5f, 0.5f, 0.5f));
                continue;
            }

            SurfaceInkManager ink = hit.collider.GetComponent<SurfaceInkManager>();
            if (ink == null)
            {
                // Orange: hit something, but it's not an inkable surface at all
                if (debugDraw) Debug.DrawLine(origin, hit.point, new Color(1f, 0.5f, 0f));
                continue;
            }

            if (Mathf.Abs(hit.normal.y) > climbThreshold)
            {
                // White: inkable, but too shallow to count as a wall (floor/ceiling/slope)
                if (debugDraw) Debug.DrawLine(origin, hit.point, Color.white);
                continue;
            }

            Vector2 uv = hit.textureCoord.sqrMagnitude > 0.0001f ? hit.textureCoord : FallbackUV(hit, ink);
            int surfTeam = ink.getSurfaceTeam(uv);
            if (surfTeam != player.Team)
            {
                // Red: a climbable wall, but not our own ink
                if (debugDraw) Debug.DrawLine(origin, hit.point, Color.red);
                sawNonOwnWall = true;
                continue;
            }

            // Green: a valid climb candidate this ray
            if (debugDraw) Debug.DrawLine(origin, hit.point, Color.green);

            // Closest wall wins, biased toward the one the player is moving into.
            float score = hit.distance - intentBias * Mathf.Max(0f, Vector3.Dot(intent, -hit.normal));
            if (score >= bestScore) continue; // valid, but not the best one so far

            found = true;
            bestScore = score;
            bestHit = hit;
            bestInk = ink;
            bestUV = uv;
        }

        // Cyan: the winning hit — this frame's actual climbNormal, drawn along the surface
        // normal so it's easy to see both where the sensor thinks the wall is and which way
        // it thinks it faces.
        if (debugDraw && found)
            Debug.DrawRay(bestHit.point, bestHit.normal * 0.5f, Color.cyan);

        return found;
    }

    // While climbing, nothing found at body height but the wall still there a little lower down
    // means we've just climbed past its top. (Rounding an outer corner, the wall ends sideways
    // instead, so this lower probe misses too and the normal grace window applies.)
    bool WallContinuesBelow()
    {
        if (!player.HasWallContact) return false;
        Vector3 origin = transform.position - Vector3.up * topProbeDrop;
        bool hit = Physics.Raycast(origin, -player.ClimbNormal, probeDistance, raycastMask);
        if (debugDraw) Debug.DrawRay(origin, -player.ClimbNormal * probeDistance, hit ? Color.magenta : Color.gray);
        return hit;
    }

    static Vector2 FallbackUV(RaycastHit hit, SurfaceInkManager inkManager)
    {
        Renderer rend = inkManager.GetComponent<Renderer>();
        if (rend == null) return Vector2.zero;
        Bounds b = rend.localBounds;
        if (b.size.sqrMagnitude < 0.0001f) return Vector2.zero;

        Vector3 p = inkManager.transform.InverseTransformPoint(hit.point);
        Vector3 n = inkManager.transform.InverseTransformDirection(hit.normal);
        float ax = Mathf.Abs(n.x), ay = Mathf.Abs(n.y), az = Mathf.Abs(n.z);

        float u, v;
        if (ay >= ax && ay >= az) {
            u = Mathf.InverseLerp(b.min.x, b.max.x, p.x);
            v = Mathf.InverseLerp(b.min.z, b.max.z, p.z);
        } else if (az >= ax) {
            u = Mathf.InverseLerp(b.min.x, b.max.x, p.x);
            v = Mathf.InverseLerp(b.min.y, b.max.y, p.y);
        } else {
            u = Mathf.InverseLerp(b.min.z, b.max.z, p.z);
            v = Mathf.InverseLerp(b.min.y, b.max.y, p.y);
        }
        return new Vector2(u, v);
    }
}

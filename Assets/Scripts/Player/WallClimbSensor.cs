using UnityEngine;

// Finds a climbable own-ink wall with a ring of short horizontal raycasts, fresh every frame,
// and reports it to the PlayerController.
public class WallClimbSensor : MonoBehaviour
{
    [SerializeField] int rayCount = 12;
    [SerializeField] float probeDistance = 0.75f;
    [SerializeField] float climbMarginDeg = 5f;  // degrees past the walkable slope limit before a surface counts as a wall
    [SerializeField] LayerMask raycastMask = ~0;
    [SerializeField] float intentBias = 0.5f;    // prefer the wall being moved into (inner corners), in metres per unit dot
    [SerializeField] float topProbeDrop = 0.25f; // how far below the ring to check whether the wall continues
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
            // A wall without our ink means the ink ran out; a wall just below means we passed its top.
            player.ClearClimbContact(atInkEdge: sawNonOwnWall, atWallTop: !sawNonOwnWall && WallContinuesBelow());
    }

    // Best own-ink wall hit: closest, biased toward the movement direction.
    // Debug colours: gray no hit, orange not inkable, white too shallow, red not our ink, green valid.
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

            if (!Physics.Raycast(origin, dir, out RaycastHit hit, probeDistance, raycastMask, QueryTriggerInteraction.Ignore))
            {
                if (debugDraw) Debug.DrawRay(origin, dir * probeDistance, new Color(0.5f, 0.5f, 0.5f));
                continue;
            }

            SurfaceInkManager ink = hit.collider.GetComponent<SurfaceInkManager>();
            if (ink == null)
            {
                if (debugDraw) Debug.DrawLine(origin, hit.point, new Color(1f, 0.5f, 0f));
                continue;
            }

            if (Mathf.Abs(hit.normal.y) > climbThreshold)
            {
                if (debugDraw) Debug.DrawLine(origin, hit.point, Color.white);
                continue;
            }

            Vector2 uv = ink.UVFromHit(hit);
            if (ink.getSurfaceTeam(uv) != player.Team)
            {
                if (debugDraw) Debug.DrawLine(origin, hit.point, Color.red);
                sawNonOwnWall = true;
                continue;
            }

            if (debugDraw) Debug.DrawLine(origin, hit.point, Color.green);

            float score = hit.distance - intentBias * Mathf.Max(0f, Vector3.Dot(intent, -hit.normal));
            if (score >= bestScore) continue;

            found = true;
            bestScore = score;
            bestHit = hit;
            bestInk = ink;
            bestUV = uv;
        }

        if (debugDraw && found)
            Debug.DrawRay(bestHit.point, bestHit.normal * 0.5f, Color.cyan); // winning wall normal

        return found;
    }

    // Wall still there a little lower down = we've climbed past its top. (At an outer corner
    // the wall ends sideways, so this misses too and the normal grace window applies.)
    bool WallContinuesBelow()
    {
        if (!player.HasWallContact) return false;
        Vector3 origin = transform.position - Vector3.up * topProbeDrop;
        bool hit = Physics.Raycast(origin, -player.ClimbNormal, probeDistance, raycastMask, QueryTriggerInteraction.Ignore);
        if (debugDraw) Debug.DrawRay(origin, -player.ClimbNormal * probeDistance, hit ? Color.magenta : Color.gray);
        return hit;
    }
}

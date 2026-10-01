using UnityEngine;

// Finds a climbable own-ink wall with a ring of short horizontal raycasts, fresh every frame,
// and reports it to the PlayerController.
public class WallClimbSensor : MonoBehaviour
{
    [SerializeField] bool debugDraw = true; // ray colours in the scene view, see TryFindWall

    const int RayCount = 12;
    const float ProbeDistance = 0.75f;

    PlayerController player;
    int inkMask; // only inkable surfaces can be climbed

    void Awake()
    {
        player = GetComponentInParent<PlayerController>();
        inkMask = LayerMask.GetMask("InkSurface");
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

        float climbThreshold = Mathf.Cos((player.SlopeLimit + 5f) * Mathf.Deg2Rad); // 5° past walkable before it's a wall
        Vector3 origin = transform.position;
        bool found = false;
        float bestScore = float.MaxValue;
        Vector3 intent = player.ClimbIntent;

        for (int i = 0; i < RayCount; i++)
        {
            float angle = i * (360f / RayCount) * Mathf.Deg2Rad;
            Vector3 dir = new Vector3(Mathf.Sin(angle), 0f, Mathf.Cos(angle));

            if (!Physics.Raycast(origin, dir, out RaycastHit hit, ProbeDistance, inkMask, QueryTriggerInteraction.Ignore))
            {
                if (debugDraw) Debug.DrawRay(origin, dir * ProbeDistance, new Color(0.5f, 0.5f, 0.5f));
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

            // Prefer the wall being moved into (inner corners): 0.5m closer per unit of push.
            float score = hit.distance - 0.5f * Mathf.Max(0f, Vector3.Dot(intent, -hit.normal));
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
        Vector3 origin = transform.position - Vector3.up * 0.25f; // a little below the ring
        bool hit = Physics.Raycast(origin, -player.ClimbNormal, ProbeDistance, inkMask, QueryTriggerInteraction.Ignore);
        if (debugDraw) Debug.DrawRay(origin, -player.ClimbNormal * ProbeDistance, hit ? Color.magenta : Color.gray);
        return hit;
    }
}

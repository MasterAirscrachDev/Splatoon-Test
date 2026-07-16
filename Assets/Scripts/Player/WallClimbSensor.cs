using UnityEngine;
using System.Collections.Generic;

// Place this on the sphere trigger child of the player.
// Probes 8 horizontal directions to detect nearby inked walls regardless of
// whether the sphere centre is inside the wall collider's bounds.
[RequireComponent(typeof(Collider))]
public class WallClimbSensor : MonoBehaviour
{
    PlayerController player;

    // Extra margin above the CharacterController's own slopeLimit before a surface counts as
    // climbable. Without this gap, a slope sitting almost exactly at slopeLimit could read as
    // "walkable" to the controller one frame and "climbable" to this sensor the next — the
    // controller's own ground handling is unreliable right at that boundary, so the two
    // systems could disagree and flicker between walking and climbing on the same slope.
    [SerializeField] float climbMarginDeg = 5f;

    // Tracks how many ink surfaces are currently inside the trigger
    // so exiting one wall doesn't clear contact while another is still active.
    readonly HashSet<Collider> activeContacts = new HashSet<Collider>();

    // 8 horizontal probe directions (world-space)
    static readonly Vector3[] Probes = {
        Vector3.forward, Vector3.back, Vector3.left, Vector3.right,
        new Vector3( 1, 0,  1).normalized, new Vector3( 1, 0, -1).normalized,
        new Vector3(-1, 0,  1).normalized, new Vector3(-1, 0, -1).normalized,
    };

    void Awake()
    {
        player = GetComponentInParent<PlayerController>();
        GetComponent<Collider>().isTrigger = true;
    }

    void OnTriggerEnter(Collider other)
    {
        if (other.GetComponent<SurfaceInkManager>() != null)
            activeContacts.Add(other);
    }

    void OnTriggerStay(Collider other)
    {
        if (player == null) return;
        SurfaceInkManager ink = other.GetComponent<SurfaceInkManager>();
        if (ink == null)
        {
            // Orange: collider in trigger but no SurfaceInkManager
            Debug.DrawLine(transform.position, other.bounds.center, Color.yellow);
            return;
        }

        Vector3 origin = transform.position;
        foreach (var dir in Probes)
        {
            if (!other.Raycast(new Ray(origin - dir * 2f, dir), out RaycastHit hit, 3f))
                continue;

            // Anything the CharacterController itself would treat as walkable ground (plus a
            // margin) is never a climbable wall, so the sensor stays in sync with slopeLimit
            // instead of using an independent hardcoded cutoff.
            float climbThreshold = Mathf.Cos((player.SlopeLimit + climbMarginDeg) * Mathf.Deg2Rad);
            if (Mathf.Abs(hit.normal.y) > climbThreshold)
            {
                // White: hit something but it's a floor/ceiling/shallow slope, not a wall
                Debug.DrawLine(origin, hit.point, Color.white);
                continue;
            }

            Vector2 uv = hit.textureCoord.sqrMagnitude > 0.0001f
                ? hit.textureCoord
                : FallbackUV(hit, ink);

            int surfTeam = ink.getSurfaceTeam(uv);
            if (surfTeam == player.Team)
            {
                // Green: wall detected and ink matches our team → climbing active
                Debug.DrawLine(origin, hit.point, Color.green);
            }
            else
            {
                // Red: wall detected but wrong team (or unpainted)
                Debug.DrawLine(origin, hit.point, Color.red);
            }

            player.SetClimbContact(hit.normal, surfTeam);
            return;
        }
    }

    void OnTriggerExit(Collider other)
    {
        activeContacts.Remove(other);
        if (activeContacts.Count == 0)
            player?.ClearClimbContact();
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

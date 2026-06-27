using UnityEngine;

public class InkEmitter : MonoBehaviour
{
    [SerializeField] int team = 1;
    [SerializeField] int splashSize = 10;

    static readonly Vector3[] Probes = {
        Vector3.up, Vector3.down, Vector3.left, Vector3.right, Vector3.forward, Vector3.back
    };

    void OnTriggerStay(Collider other)
    {
        SurfaceInkManager ink = other.GetComponent<SurfaceInkManager>();
        if (ink == null) return;

        foreach (Vector3 dir in Probes)
        {
            RaycastHit hit;
            if (other.Raycast(new Ray(transform.position - dir * 2f, dir), out hit, 3f))
            {
                Vector2 uv = hit.textureCoord;
                if (uv.sqrMagnitude < 0.0001f)
                    uv = FallbackUV(hit, ink);
                ink.Splat(uv, splashSize, team);
                return; // one splat per collider per physics tick
            }
        }
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
        if (ay >= ax && ay >= az)
        {
            u = Mathf.InverseLerp(b.min.x, b.max.x, p.x);
            v = Mathf.InverseLerp(b.min.z, b.max.z, p.z);
        }
        else if (az >= ax)
        {
            u = Mathf.InverseLerp(b.min.x, b.max.x, p.x);
            v = Mathf.InverseLerp(b.min.y, b.max.y, p.y);
        }
        else
        {
            u = Mathf.InverseLerp(b.min.z, b.max.z, p.z);
            v = Mathf.InverseLerp(b.min.y, b.max.y, p.y);
        }
        return new Vector2(u, v);
    }
}

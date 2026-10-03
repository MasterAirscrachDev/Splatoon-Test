using UnityEngine;

// Splats any ink surface its trigger overlaps (e.g. the ink-exit splash).
[RequireComponent(typeof(Collider))]
[RequireComponent(typeof(Rigidbody))]
public class InkEmitter : MonoBehaviour
{
    [SerializeField] int team = 1;
    [SerializeField] int splashSize = 10;
    bool instaClear; // destroy after the first splat

    static readonly Vector3[] Probes = {
        Vector3.up, Vector3.down, Vector3.left, Vector3.right, Vector3.forward, Vector3.back
    };

    public void Setup(int team, int splashSize, bool instaClear = false)
    {
        this.team = team;
        this.splashSize = splashSize;
        this.instaClear = instaClear;
        if (instaClear) Destroy(gameObject, 2f); // in case it never touches a surface
    }

    void OnTriggerStay(Collider other)
    {
        SurfaceInkManager ink = other.GetComponent<SurfaceInkManager>();
        if (ink == null) return;

        foreach (Vector3 dir in Probes)
        {
            if (other.Raycast(new Ray(transform.position - dir * 2f, dir), out RaycastHit hit, 3f))
            {
                ink.SplatAt(hit, splashSize, team);
                if (instaClear) Destroy(gameObject);
                return; // one splat per collider per physics tick
            }
        }
    }
}

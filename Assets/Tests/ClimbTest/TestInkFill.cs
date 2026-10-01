using System.Collections;
using UnityEngine;

// Paints fixed regions of a surface with known ink at scene start, so test stations begin in
// an exact state (own-ink wall, enemy wall, half-painted wall...) instead of relying on splats.
[RequireComponent(typeof(SurfaceInkManager))]
public class TestInkFill : MonoBehaviour
{
    [System.Serializable]
    public struct Region
    {
        public Rect uv;  // UV-space rectangle
        public int team; // 0 = none, 1 = alpha, 2 = beta
    }

    public Region[] regions = new Region[0];

    IEnumerator Start()
    {
        yield return null; // SurfaceInkManager creates its textures in Start; let every Start run first
        SurfaceInkManager ink = GetComponent<SurfaceInkManager>();
        foreach (Region r in regions) ink.FillRegion(r.uv, r.team);
    }
}

using System.Collections;
using UnityEngine;

// Paints fixed regions of a surface at scene start, so test stations begin in a known ink state.
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
        yield return null; // after SurfaceInkManager.Start creates its textures
        SurfaceInkManager ink = GetComponent<SurfaceInkManager>();
        foreach (Region r in regions) ink.FillRegion(r.uv, r.team);
    }
}

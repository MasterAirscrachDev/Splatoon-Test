using UnityEngine;

// Frames an orthographic camera on the level (every enabled mesh on the given layers) from a
// raised angle: pitch below horizontal, yaw turning the level on screen. Used by the map and the
// results view.
public static class LevelView
{
    public static void Frame(Camera cam, int boundsMask, float pitch, float yaw, float aspect, float padding)
    {
        Transform t = cam.transform;
        t.SetPositionAndRotation(Vector3.zero, Quaternion.Euler(pitch, yaw, 0f));

        Vector3 min = Vector3.one * float.MaxValue, max = -min;
        foreach (MeshRenderer r in Object.FindObjectsByType<MeshRenderer>(FindObjectsSortMode.None))
        {
            if (!r.enabled || (boundsMask & (1 << r.gameObject.layer)) == 0) continue;
            Bounds b = r.bounds;
            for (int i = 0; i < 8; i++)
            {
                Vector3 corner = new Vector3(i % 2 == 0 ? b.min.x : b.max.x, i / 2 % 2 == 0 ? b.min.y : b.max.y, i / 4 == 0 ? b.min.z : b.max.z);
                Vector3 local = t.InverseTransformPoint(corner); // camera space
                min = Vector3.Min(min, local);
                max = Vector3.Max(max, local);
            }
        }
        if (min.x > max.x) { min = -Vector3.one * 25f; max = Vector3.one * 25f; } // nothing on those layers

        cam.orthographic = true;
        cam.aspect = aspect;
        cam.orthographicSize = Mathf.Max((max.y - min.y) / 2f, (max.x - min.x) / 2f / aspect) * padding;
        // Centred on the level, backed off in front of its nearest point.
        t.position = t.TransformPoint(new Vector3((min.x + max.x) / 2f, (min.y + max.y) / 2f, min.z - 10f));
        cam.nearClipPlane = 0.1f;
        cam.farClipPlane = max.z - min.z + 20f;
    }
}

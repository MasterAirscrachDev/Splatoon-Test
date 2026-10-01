using UnityEngine;

// Single source of truth for the climb test scene's geometry, shared by the editor builder
// (ClimbTestSceneBuilder, which places everything) and the runtime ClimbTestRunner (whose
// assertions depend on where walls actually are). All positions are station-local: each
// station sits on its own floor whose top surface is y = 0, with the wall under test (if any)
// having its front face on the z = 0 plane, facing -Z towards the spawn point.
public static class ClimbTestLayout
{
    public enum Station { Lobby, FlatWall, Ledge, Pillar, InnerCorner, NeutralWall, EnemyWall, PartialWall, Ramp30, Ramp60 }

    public const float StationSpacing = 30f;
    public const float FloorWidth = 30f, FloorDepth = 24f, FloorThickness = 0.5f;

    public const float WallHeight = 10f;        // FlatWall, Pillar, InnerCorner, Neutral/Enemy/PartialWall
    public const float LedgeHeight = 4f;
    public const float PartialInkHeight = 5f;   // PartialWall's front face is own-ink from the floor up to here only
    public const float PillarWidth = 4f;        // Pillar spans x = ±PillarWidth/2
    public const float InnerCornerSideX = 2f;   // InnerCorner's side wall inner face (normal -X) sits on this plane

    public const float Ramp30Length = 12f, Ramp60Length = 8f, RampWidth = 6f, RampThickness = 0.5f;

    public static string Name(Station s) => "Station_" + s;
    public static Vector3 Origin(Station s) => new Vector3((int)s * StationSpacing, 0f, 0f);

    // Rotation of a ramp that rises towards +Z by `angle` degrees.
    public static Quaternion RampRotation(float angle) => Quaternion.Euler(-angle, 0f, 0f);

    // Local centre of a ramp placed so its top surface meets the floor exactly along z = 0.
    public static Vector3 RampCenter(float angle, float length)
    {
        Vector3 lowTopEdge = new Vector3(0f, RampThickness / 2f, -length / 2f);
        return -(RampRotation(angle) * lowTopEdge);
    }

    // A point on a ramp's top surface, `along` metres up-slope from its low edge.
    public static Vector3 RampSurfacePoint(float angle, float length, float along)
    {
        Vector3 local = new Vector3(0f, RampThickness / 2f, -length / 2f + along);
        return RampCenter(angle, length) + RampRotation(angle) * local;
    }
}

// Box mesh layout used for every test surface: each face gets its own UV island (a 3x2 grid),
// unlike Unity's built-in cube where all six faces share one UV square and so can't be
// painted independently. On side faces u runs left→right as seen from outside and v runs
// bottom→top, so "fill the bottom half of the front face" is just a sub-rect of its island.
public static class ClimbTestBox
{
    public enum Face { Front, Right, Back, Left, Top, Bottom } // Front = -Z

    const float Margin = 0.01f; // keeps bilinear filtering from bleeding ink across islands

    public static Rect FaceRect(Face f)
    {
        int i = (int)f, col = i % 3, row = i / 3;
        return new Rect(col / 3f + Margin, row / 2f + Margin, 1f / 3f - 2f * Margin, 0.5f - 2f * Margin);
    }

    // Bottom `fraction` (0..1) of a side face's island.
    public static Rect FaceRectBottom(Face f, float fraction)
    {
        Rect r = FaceRect(f);
        return new Rect(r.x, r.y, r.width, r.height * Mathf.Clamp01(fraction));
    }

    // Outward normal plus the u (right) and v (up) axes as seen looking at the face from outside.
    public static void FaceAxes(Face f, out Vector3 n, out Vector3 u, out Vector3 v)
    {
        switch (f)
        {
            case Face.Front: n = Vector3.back;    u = Vector3.right; v = Vector3.up;      break;
            case Face.Right: n = Vector3.right;   u = Vector3.forward; v = Vector3.up;    break;
            case Face.Back:  n = Vector3.forward; u = Vector3.left;  v = Vector3.up;      break;
            case Face.Left:  n = Vector3.left;    u = Vector3.back;  v = Vector3.up;      break;
            case Face.Top:   n = Vector3.up;      u = Vector3.right; v = Vector3.forward; break;
            default:         n = Vector3.down;    u = Vector3.right; v = Vector3.back;    break;
        }
    }
}

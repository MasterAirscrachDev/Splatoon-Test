using UnityEngine;

// How a thrown sub (sprinkler, bombs) leaves the hand: launched along the aim, tipped up by `angle`,
// at `speed`, then falling under `gravity` until it reaches something. Its range: more speed throws
// further; more angle lobs it higher (and further, up to about 45° all told); more gravity drops it
// sooner. Each thrown sub's prefab has its own.
[System.Serializable]
public class SubThrow
{
    [Tooltip("Top launch speed (metres per second): how far it can reach. Thrown at something nearer, it's thrown softer to land on it")] public float speed = 16f;
    [Tooltip("Degrees above the aim it leaves at: a lob. Aiming level, about 25-35 throws furthest for a given speed (it starts above the ground)")] public float angle = 25f;
    [Tooltip("Gravity on it in flight (metres per second²): more drops it sooner, for a shorter, snappier arc")] public float gravity = 20f;

    const float SteepestLaunch = 85f; // looking well up still throws up, not over our head

    // The launch velocity for an aim (the view's forward).
    public Vector3 Velocity(Vector3 aim)
    {
        aim = aim.sqrMagnitude > 1e-6f ? aim.normalized : Vector3.forward;
        Vector3 flat = Vector3.ProjectOnPlane(aim, Vector3.up);
        flat = flat.sqrMagnitude > 1e-6f ? flat.normalized : Vector3.forward;
        float up = Mathf.Min(Mathf.Asin(Mathf.Clamp(aim.y, -1f, 1f)) * Mathf.Rad2Deg + angle, SteepestLaunch) * Mathf.Deg2Rad;
        return (flat * Mathf.Cos(up) + Vector3.up * Mathf.Sin(up)) * speed;
    }

    // The launch velocity from `from` to come down on `target` (what the reticle's on): headed at it,
    // lobbed `angle` above the line to it (so the same arc wherever it's aimed), with the speed solved
    // to land there, but never more than `speed` (past its reach it falls short). No target (aimed at
    // the sky): thrown at full speed, `angle` above the aim.
    public Vector3 VelocityTo(Vector3 from, Vector3 aim, Vector3? target)
    {
        Vector3 full = Velocity(aim);
        if (!target.HasValue) return full;
        Vector3 d = target.Value - from;
        Vector3 flat = new Vector3(d.x, 0f, d.z);
        float x = flat.magnitude, y = d.y;
        if (x < 0.05f) return full;
        float up = Mathf.Min(Mathf.Atan2(y, x) * Mathf.Rad2Deg + angle, SteepestLaunch) * Mathf.Deg2Rad;
        float cos = Mathf.Cos(up), sin = Mathf.Sin(up);
        Vector3 dir = flat / x * cos + Vector3.up * sin;
        float fall = 2f * cos * cos * (x * sin / Mathf.Max(cos, 1e-4f) - y); // x tan(up) - y: how far above the target the launch line passes
        if (cos < 1e-3f || fall <= 1e-4f) return dir * speed;                  // the target's above its line: as hard as it can
        return dir * Mathf.Min(Mathf.Sqrt(gravity * x * x / fall), speed);
    }

    // How far it lands from where it's thrown, aimed level, starting `height` above the ground.
    public float LevelRange(float height)
    {
        float up = angle * Mathf.Deg2Rad, vx = speed * Mathf.Cos(up), vy = speed * Mathf.Sin(up);
        float t = (vy + Mathf.Sqrt(vy * vy + 2f * gravity * Mathf.Max(0f, height))) / Mathf.Max(gravity, 0.01f);
        return vx * t;
    }
}

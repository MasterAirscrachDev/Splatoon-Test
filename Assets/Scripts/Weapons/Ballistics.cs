using UnityEngine;

// How a shot flies after launch, over its age in seconds: its speed along the launch direction (a
// multiple of the starting speed) and the gravity pulling it down (a multiple of normal gravity).
// Both at 1 is a plain throw. Speeding a shot up by k without changing its path: velocity ×k and
// gravity ×k² (with the curve times ÷k).
[System.Serializable]
public class Ballistics
{
    public AnimationCurve velocityOverTime = AnimationCurve.Constant(0f, 1f, 1f);
    public AnimationCurve gravityOverTime = AnimationCurve.Constant(0f, 1f, 1f);

    public static readonly Ballistics Default = new Ballistics();

    // The same path flown k times as fast (launch it at k times the speed): gravity x k², both
    // curves over 1/k of the time.
    public Ballistics Faster(float k)
    {
        if (Mathf.Approximately(k, 1f) || k <= 0f) return this;
        return new Ballistics
        {
            velocityOverTime = Retimed(velocityOverTime, k, 1f),
            gravityOverTime = Retimed(gravityOverTime, k, k * k),
        };
    }

    static AnimationCurve Retimed(AnimationCurve curve, float k, float valueScale)
    {
        Keyframe[] keys = curve.keys;
        for (int i = 0; i < keys.Length; i++)
        {
            keys[i].time /= k;
            keys[i].value *= valueScale;
            keys[i].inTangent *= valueScale * k;  // slopes: value over time
            keys[i].outTangent *= valueScale * k;
        }
        return new AnimationCurve(keys) { preWrapMode = curve.preWrapMode, postWrapMode = curve.postWrapMode };
    }
}

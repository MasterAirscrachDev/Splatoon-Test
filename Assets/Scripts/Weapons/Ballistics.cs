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
}

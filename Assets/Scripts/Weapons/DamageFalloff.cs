using UnityEngine;
using UnityEngine.Serialization;

// A shot's damage over its flight: `damage` scaled by scaleOverTime at the shot's age (x: seconds
// it's flown, y: damage multiplier, held past the last key). Every weapon's shots fall off like
// this: close-range hits land hard, long-range ones weaker. A flat curve: always `damage`.
[System.Serializable]
public class DamageFalloff
{
    [FormerlySerializedAs("maxDamage")]
    public float damage = 30f;                                               // at launch (scale 1)
    public AnimationCurve scaleOverTime = AnimationCurve.Constant(0f, 1f, 1f);

    public DamageFalloff() { }
    public DamageFalloff(float damage, AnimationCurve scaleOverTime) { this.damage = damage; this.scaleOverTime = scaleOverTime; }

    // Full damage for `hold` seconds, then straight down to `minScale` by `until`.
    public static AnimationCurve HoldThenFall(float hold, float until, float minScale)
    {
        float slope = until > hold ? (minScale - 1f) / (until - hold) : 0f;
        if (hold <= 0f) return new AnimationCurve(new Keyframe(0f, 1f, 0f, slope), new Keyframe(until, minScale, slope, 0f));
        return new AnimationCurve(new Keyframe(0f, 1f, 0f, 0f), new Keyframe(hold, 1f, 0f, slope), new Keyframe(until, minScale, slope, 0f));
    }

    public float Scale(float age) => scaleOverTime != null && scaleOverTime.length > 0 ? Mathf.Max(0f, scaleOverTime.Evaluate(age)) : 1f;
    public float At(float age) => damage * Scale(age);

    // The lowest it gets (the curve's smallest key), for checks.
    public float MinDamage
    {
        get
        {
            if (scaleOverTime == null || scaleOverTime.length == 0) return damage;
            float min = float.MaxValue;
            foreach (Keyframe k in scaleOverTime.keys) min = Mathf.Min(min, k.value);
            return damage * Mathf.Max(0f, min);
        }
    }
}

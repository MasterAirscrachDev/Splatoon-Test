using UnityEngine;

// A shot's damage over its flight: maxDamage as it leaves, easing linearly to minDamage over
// scaleTime seconds and staying there. Close-range hits land hard, long-range ones weaker.
// scaleTime 0: always maxDamage.
[System.Serializable]
public class DamageFalloff
{
    public float maxDamage = 30f;  // at launch
    public float minDamage = 30f;  // from scaleTime on
    public float scaleTime = 0f;   // seconds

    public DamageFalloff() { }
    public DamageFalloff(float max, float min, float time) { maxDamage = max; minDamage = min; scaleTime = time; }

    public float At(float age) => scaleTime > 0f ? Mathf.Lerp(maxDamage, minDamage, age / scaleTime) : maxDamage;
}

using UnityEditor;
using UnityEngine;

// Every weapon's loadout-card ratings (Tools/Weapons/Set Loadout Ratings): rough 0-1 power, range and
// speed for the card's bars, by eye from each weapon's tuning. A new weapon needs a row here.
public static class WeaponRatings
{
    const string Prefabs = "Assets/Prefabs/Weapons/";

    static readonly (string prefab, float power, float range, float speed)[] Ratings =
    {
        ("AirspraySE",     0.30f, 0.35f, 0.90f),
        ("Inkshot",        0.50f, 0.60f, 0.60f),
        ("SolarBlaster",   0.85f, 0.45f, 0.30f),
        ("ArtemisBlaster", 0.45f, 0.70f, 0.50f),
        ("Inkroller",      0.75f, 0.35f, 0.50f),
        ("EraRoller",      0.30f, 0.35f, 0.80f),
        ("HeavyRoller",    0.95f, 0.75f, 0.20f),
        ("Inkbrush",       0.25f, 0.20f, 0.95f),
        ("Bucket",         0.80f, 0.55f, 0.40f),
    };

    [MenuItem("Tools/Weapons/Set Loadout Ratings")]
    public static void SetRatingsMenu() => Debug.Log(SetRatings());

    public static string SetRatings()
    {
        int set = 0;
        foreach (var (prefab, power, range, speed) in Ratings)
        {
            var weapon = AssetDatabase.LoadAssetAtPath<Weapon>(Prefabs + prefab + ".prefab");
            if (weapon == null) continue;
            var so = new SerializedObject(weapon);
            so.FindProperty("powerRating").floatValue = power;
            so.FindProperty("rangeRating").floatValue = range;
            so.FindProperty("speedRating").floatValue = speed;
            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(weapon);
            set++;
        }
        return $"ratings on {set} weapons";
    }
}

using UnityEngine;

// Per-map settings: one on each map's root. A scene without one is a map that can host matches
// (Map1).
public class MapSettings : MonoBehaviour
{
    [SerializeField] bool matchesAllowed = true;  // false on the lobby: free roam only, pick a map to play

    static MapSettings current;
    public static bool MatchesAllowed => current == null || current.matchesAllowed;

    void OnEnable() => current = this;
    void OnDisable() { if (current == this) current = null; }

    public void Set(bool matchesAllowed) => this.matchesAllowed = matchesAllowed; // tests
}

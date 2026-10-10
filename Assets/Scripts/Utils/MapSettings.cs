using UnityEngine;

// Per-map settings: one on each map's root. A scene without one is a two-team map that can host
// matches (Map1).
public class MapSettings : MonoBehaviour
{
    [SerializeField] bool matchesAllowed = true;  // false on the lobby: free roam only, pick a map to play
    [SerializeField, Range(2, Teams.Max)] int teamCount = 2; // how many teams play here (a spawn pad each)

    static MapSettings current;
    public static bool MatchesAllowed => current == null || current.matchesAllowed;
    public static int TeamCount => current == null ? 2 : UnityEngine.Mathf.Clamp(current.teamCount, 2, Teams.Max);

    void OnEnable() => current = this;
    void OnDisable() { if (current == this) current = null; }

    public void Set(bool matchesAllowed) => this.matchesAllowed = matchesAllowed; // tests
    public void SetTeamCount(int count) => teamCount = count;                      // tests
}

// Teams 1..Max (Alpha, Beta, Gamma, Delta); 0 is no team (spectators, neutral turf). How many play
// is the map's (MapSettings.TeamCount, 2 unless a map says otherwise), and so is their size: 4v4,
// 3v3v3 or 2v2v2v2. Everyone else spectates (as many as like).
public static class Teams
{
    public const int Max = 4;
    static readonly string[] names = { "", "Alpha", "Beta", "Gamma", "Delta" };

    public static int Count => MapSettings.TeamCount;
    public static int SlotsPerTeam => Count switch { 2 => 4, 3 => 3, _ => 2 };
    public static int Capacity => Count * SlotsPerTeam;
    public static bool Valid(int team) => team >= 1 && team <= Max;
    public static string Name(int team) => Valid(team) ? names[team] : "";
    public static string SpawnTag(int team) => Valid(team) ? names[team] + "Spawn" : "";

    // Roster roles for the teams (PlayerRole keeps its wire values: Gamma and Delta come after the
    // non-playing roles; NotPlaying is no longer used, and counts as spectating).
    public static PlayerRole Role(int team) => team switch
    {
        1 => PlayerRole.Alpha, 2 => PlayerRole.Beta, 3 => PlayerRole.Gamma, 4 => PlayerRole.Delta,
        _ => PlayerRole.Spectator,
    };

    public static int Of(PlayerRole role) => role switch
    {
        PlayerRole.Alpha => 1, PlayerRole.Beta => 2, PlayerRole.Gamma => 3, PlayerRole.Delta => 4,
        _ => 0,
    };

    // A new player's team: round-robin by join order over the map's teams until they're full, then
    // 0 (spectating).
    public static int ForJoinOrder(int index) => index < 0 || index >= Capacity ? 0 : 1 + index % Count;
}

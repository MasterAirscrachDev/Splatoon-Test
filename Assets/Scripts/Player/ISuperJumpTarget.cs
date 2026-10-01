using UnityEngine;

// Somewhere a player can Super Jump to: a teammate, a team beacon or the team spawn.
public interface ISuperJumpTarget
{
    Vector3 JumpPosition { get; }
    bool IsValidTargetFor(PlayerController jumper);
    void OnSuperJumpLanded(PlayerController jumper);
}

// A team's spawn: always available to its own players.
public class SpawnJumpTarget : ISuperJumpTarget
{
    readonly int team;

    public SpawnJumpTarget(int team, Vector3 position)
    {
        this.team = team;
        JumpPosition = position;
    }

    public Vector3 JumpPosition { get; }
    public bool IsValidTargetFor(PlayerController jumper) => jumper != null && jumper.Team == team;
    public void OnSuperJumpLanded(PlayerController jumper) { }
}

public static class SuperJumpTargets
{
    // Interface references bypass Unity's destroyed-object check.
    public static bool Alive(ISuperJumpTarget target) => target is Object o ? o != null : target != null;
}

public static class InputGate
{
    // A menu is open: gameplay ignores hardware input (look, move, swim, jump, fire).
    public static bool Blocked;

    // Match countdown / time's up / results: players can look around but not act (move, swim,
    // jump, fire, sub, special, Super Jump). Applies to scripted test input too.
    public static bool MatchLocked;

    [UnityEngine.RuntimeInitializeOnLoadMethod(UnityEngine.RuntimeInitializeLoadType.SubsystemRegistration)]
    static void Reset() => Blocked = MatchLocked = false;
}

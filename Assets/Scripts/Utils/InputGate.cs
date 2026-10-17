public static class InputGate
{
    // A menu is open: gameplay ignores hardware input (look, move, swim, jump, fire).
    public static bool Blocked;

    // The gamelock (match countdown / time's up / results): players can look around but not act
    // (move, swim, jump, fire, sub, special, Super Jump; scripted test input too), can't be hurt or
    // splatted (falls included), and no surface takes ink (shots still in the air land harmlessly).
    public static bool GameLocked;

    [UnityEngine.RuntimeInitializeOnLoadMethod(UnityEngine.RuntimeInitializeLoadType.SubsystemRegistration)]
    static void Reset() => Blocked = GameLocked = false;
}

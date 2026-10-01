// Set while a menu is open, so gameplay ignores hardware input (look, move, swim, jump, fire).
public static class InputGate
{
    public static bool Blocked;

    [UnityEngine.RuntimeInitializeOnLoadMethod(UnityEngine.RuntimeInitializeLoadType.SubsystemRegistration)]
    static void Reset() => Blocked = false;
}

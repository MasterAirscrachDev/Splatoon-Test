using System;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.DualShock;
using UnityEngine.InputSystem.Switch;
using UnityEngine.InputSystem.Utilities;
using UnityEngine.InputSystem.XInput;

public enum ControlScheme { KeyboardMouse, Gamepad }
public enum PadFamily { Xbox, PlayStation, Nintendo }

// Which kind of input the player is using, for showing the right control icons: whatever last had
// a key or button pressed (or a gamepad stick pushed). Mouse movement alone never switches it, so
// gyro-as-mouse from Steam Input doesn't flip a controller player over to keyboard icons. The
// gamepad's family (Xbox, PlayStation, Nintendo) picks the face-button art.
public static class InputMode
{
    public static ControlScheme Scheme { get; private set; } = ControlScheme.KeyboardMouse;
    public static PadFamily Family { get; private set; } = PadFamily.Xbox;
    public static string Group => Scheme == ControlScheme.Gamepad ? "Gamepad" : "Keyboard&Mouse"; // binding group in ControlLayer
    public static event Action Changed;

    const float StickSwitch = 0.5f; // a stick pushed this far counts as using the gamepad

    static ControlLayer actions;
    static IDisposable buttonWatch;

    // The game's actions, for reading their bindings (never enabled).
    public static ControlLayer Actions => actions ??= new ControlLayer();

    public static void Use(ControlScheme scheme, PadFamily family)
    {
        if (scheme == Scheme && family == Family) return;
        Scheme = scheme;
        Family = family;
        Changed?.Invoke();
    }

    public static PadFamily FamilyOf(Gamepad pad)
    {
        if (pad is DualShockGamepad) return PadFamily.PlayStation;
        if (pad is SwitchProControllerHID) return PadFamily.Nintendo;
        if (pad is XInputController && SteamGyro.Family is PadFamily steam) return steam; // Steam Input shows every pad as Xbox
        return PadFamily.Xbox;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        buttonWatch?.Dispose();
        buttonWatch = null;
        InputSystem.onAfterUpdate -= WatchSticks;
        actions?.Dispose();
        actions = null;
        Changed = null;
        Scheme = ControlScheme.KeyboardMouse;
        Family = PadFamily.Xbox;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void Watch()
    {
        buttonWatch = InputSystem.onAnyButtonPress.Call(OnButton);
        InputSystem.onAfterUpdate += WatchSticks;
    }

    static void OnButton(InputControl control)
    {
        if (GameCursor.IsVirtual(control.device)) return; // the gamepad clicking through the cursor
        if (control.device is Gamepad pad) Use(ControlScheme.Gamepad, FamilyOf(pad));
        else if (control.device is Keyboard || control.device is Mouse) Use(ControlScheme.KeyboardMouse, Family);
    }

    static void WatchSticks()
    {
        foreach (Gamepad pad in Gamepad.all)
            if (pad.leftStick.ReadValue().sqrMagnitude > StickSwitch * StickSwitch || pad.rightStick.ReadValue().sqrMagnitude > StickSwitch * StickSwitch)
            {
                Use(ControlScheme.Gamepad, FamilyOf(pad));
                return;
            }
    }
}

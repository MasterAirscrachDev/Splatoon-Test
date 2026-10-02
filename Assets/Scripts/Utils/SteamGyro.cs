using System;
using System.Runtime.InteropServices;
using UnityEngine;

// Gyro from Steam Input: the controller's rotation rate (degrees per second, as turn and up/down)
// read straight from Steam's API, since Unity's Input System doesn't expose gyros on PC and
// Facepunch.Steamworks keeps its motion call internal. Steam Input has to be handling the
// controller (on by default for PlayStation and Switch pads once the player enables their support
// in Steam), and its own gyro-as-mouse should be off for this game or the view moves twice.
// Also says what the controller really is, since Steam Input shows every pad to Unity as Xbox.
// Polled once per frame on first use. Tests set TestRate instead.
public static class SteamGyro
{
#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
    const string Lib = "steam_api64";
#else
    const string Lib = "libsteam_api";
#endif

    // This Steamworks build's interface (SteamInput001): Init and RunFrame take no arguments.
    [DllImport(Lib, EntryPoint = "SteamAPI_SteamInput_v001", CallingConvention = CallingConvention.Cdecl)]
    static extern IntPtr SteamInputInterface();
    [DllImport(Lib, EntryPoint = "SteamAPI_ISteamInput_Init", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    static extern bool Init(IntPtr self);
    [DllImport(Lib, EntryPoint = "SteamAPI_ISteamInput_RunFrame", CallingConvention = CallingConvention.Cdecl)]
    static extern void RunFrame(IntPtr self);
    [DllImport(Lib, EntryPoint = "SteamAPI_ISteamInput_GetConnectedControllers", CallingConvention = CallingConvention.Cdecl)]
    static extern int GetConnectedControllers(IntPtr self, [In, Out] ulong[] handles);
    [DllImport(Lib, EntryPoint = "SteamAPI_ISteamInput_GetMotionData", CallingConvention = CallingConvention.Cdecl)]
    static extern MotionData GetMotionData(IntPtr self, ulong handle);
    [DllImport(Lib, EntryPoint = "SteamAPI_ISteamInput_GetInputTypeForHandle", CallingConvention = CallingConvention.Cdecl)]
    static extern int GetInputTypeForHandle(IntPtr self, ulong handle);

    // InputMotionData_t. Angular velocity runs -32767..32767 for -2000..2000 degrees per second;
    // X is the controller's pitch, Y its roll, Z its yaw.
    [StructLayout(LayoutKind.Sequential)]
    struct MotionData
    {
        public float rotQuatX, rotQuatY, rotQuatZ, rotQuatW;
        public float posAccelX, posAccelY, posAccelZ;
        public float rotVelX, rotVelY, rotVelZ;
    }

    const float RawToDegrees = 2000f / 32767f;
    const float Noise = 0.5f;          // degrees per second treated as holding still
    const int MaxControllers = 16;     // STEAM_INPUT_MAX_COUNT

    // ESteamInputType values (newer ones too: the Steam client knows more than this SDK's enum).
    public const int PS4 = 5, SwitchJoyConPair = 8, SwitchJoyConSingle = 9, SwitchPro = 10, PS3 = 12, PS5 = 13;

    static readonly ulong[] handles = new ulong[MaxControllers];
    static IntPtr steamInput;
    static bool broken;            // the library or an export is missing: stop trying
    static int polledFrame = -1, lastType = -1;
    static Vector2 rate;
    static int controllerType = -1;
    static bool seenMotion;

    public static Vector2? TestRate;                               // tests: pretend the gyro turns at this
    public static bool Enabled = true;                             // gyro aiming on (settings)

    // Turn (x, + right) and up/down (y, + up), degrees per second; zero without a gyro.
    public static Vector2 Rate { get { Poll(); return rate; } }
    public static bool Moving => Rate.sqrMagnitude > Noise * Noise;
    public static bool HasGyro { get { Poll(); return seenMotion; } } // this controller has shown gyro motion
    public static int ControllerType { get { Poll(); return controllerType; } } // -1: none through Steam Input

    // The art to show for a Steam Input controller, if Steam knows what it is.
    public static PadFamily? Family => FamilyFor(ControllerType);

    public static PadFamily? FamilyFor(int steamInputType)
    {
        int t = steamInputType;
        if (t == PS3 || t == PS4 || t == PS5) return PadFamily.PlayStation;
        if (t == SwitchPro || t == SwitchJoyConPair || t == SwitchJoyConSingle) return PadFamily.Nintendo;
        return t < 0 ? (PadFamily?)null : PadFamily.Xbox; // Xbox, Steam Controller/Deck, generic
    }

    // Tests: back to reading Steam, forgetting any pretend gyro.
    public static void EndTest()
    {
        TestRate = null;
        seenMotion = false;
        polledFrame = -1;
    }

    static void Poll()
    {
        if (polledFrame == Time.frameCount) return;
        polledFrame = Time.frameCount;
        if (TestRate.HasValue) { rate = Enabled ? TestRate.Value : Vector2.zero; seenMotion |= rate != Vector2.zero; return; }
        rate = Vector2.zero;
        controllerType = -1;
        if (broken || !Steamworks.SteamClient.IsValid) return;
        try
        {
            if (steamInput == IntPtr.Zero)
            {
                steamInput = SteamInputInterface();
                if (steamInput == IntPtr.Zero || !Init(steamInput)) { steamInput = IntPtr.Zero; return; }
            }
            RunFrame(steamInput);
            int count = GetConnectedControllers(steamInput, handles);
            if (count <= 0) return;
            ulong handle = handles[0]; // the first controller Steam Input reports
            int type = GetInputTypeForHandle(steamInput, handle);
            if (type != lastType) { seenMotion = false; lastType = type; } // a different controller
            controllerType = type;
            if (!Enabled) return;
            MotionData m = GetMotionData(steamInput, handle);
            Vector2 r = new Vector2(-m.rotVelZ, m.rotVelX) * RawToDegrees; // yaw left is +Z: turning right is -Z
            rate = r.sqrMagnitude > Noise * Noise ? r : Vector2.zero;
            seenMotion |= rate != Vector2.zero;
        }
        catch (Exception e) when (e is DllNotFoundException || e is EntryPointNotFoundException)
        {
            broken = true;
            Debug.LogWarning($"[SteamGyro] Steam Input isn't available ({e.Message}); no gyro.");
        }
    }

    // Tests: every Steam call resolves in the library (no call is made).
    public static string CheckBindings()
    {
        try
        {
            foreach (string name in new[] { nameof(SteamInputInterface), nameof(Init), nameof(RunFrame), nameof(GetConnectedControllers), nameof(GetMotionData), nameof(GetInputTypeForHandle) })
                Marshal.Prelink(typeof(SteamGyro).GetMethod(name, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static));
            return null;
        }
        catch (Exception e) { return e.GetType().Name + ": " + e.Message; }
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        steamInput = IntPtr.Zero;
        broken = false;
        polledFrame = -1;
        rate = Vector2.zero;
        controllerType = lastType = -1;
        seenMotion = false;
        TestRate = null;
        Enabled = true;
    }
}

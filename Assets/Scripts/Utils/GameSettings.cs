using System;
using System.Threading.Tasks;
using UnityEngine;

// The player's settings, saved with FileSuper to %AppData%\<company>\<product>\settings.cfg (plain
// text, so hand-editable). Sensitivities multiply the player prefab's tuned values (1 = as tuned).
// Loaded once at startup (defaults until the file's read); saved when the settings panel closes.
public static class GameSettings
{
    public const float MinSensitivity = 0.1f, MaxSensitivity = 3f;

    public static float MouseSensitivity = 1f;
    public static float StickSensitivity = 1f;   // the right stick's turn rate
    public static float GyroSensitivityX = 1f;   // turning
    public static float GyroSensitivityY = 1f;   // looking up and down
    public static bool GyroEnabled = true;       // ignored without a gyro
    public static float MusicVolume = DefaultMusicVolume; // 0..1, the slider's place (played at MusicGain)
    public static float SfxVolume = 1f;          // 0..1, likewise (SfxGain)
    public const float DefaultMusicVolume = 0.85f; // about half as loud as full (-6 dB)

    // A volume slider's place (0..1) to the loudness it plays at: even steps in decibels, as hearing
    // works (100% is 0 dB, each 10% below is VolumeRangeDb / 10 quieter), and 0 is silent.
    public const float VolumeRangeDb = 40f;
    public static float Gain(float slider) => slider <= 0f ? 0f : Mathf.Pow(10f, (Mathf.Clamp01(slider) - 1f) * VolumeRangeDb / 20f);
    public static float MusicGain => Gain(MusicVolume);
    public static float SfxGain => Gain(SfxVolume);

    public static string FileName = "settings.cfg"; // tests use their own
    public static bool Loaded { get; private set; }
    public static event Action Changed;

    static FileSuper files;
    static FileSuper Files => files ??= new FileSuper(Application.productName, Application.companyName);

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void LoadAtStartup() => _ = Load();

    public static void ResetToDefaults()
    {
        MouseSensitivity = StickSensitivity = GyroSensitivityX = GyroSensitivityY = 1f;
        GyroEnabled = true;
        MusicVolume = DefaultMusicVolume;
        SfxVolume = 1f;
        Apply();
    }

    // Tell everything that reads them (and the gyro) that they changed.
    public static void Apply()
    {
        SteamGyro.Enabled = GyroEnabled;
        Changed?.Invoke();
    }

    public static async Task Load()
    {
        Save save = null;
        try { save = await Files.LoadFile(FileName); }
        catch (Exception e) { Debug.LogWarning($"[Settings] couldn't read {FileName}: {e.Message}"); }
        if (save != null)
        {
            MouseSensitivity = Clamp(save.GetVar("mouseSensitivity", MouseSensitivity));
            StickSensitivity = Clamp(save.GetVar("stickSensitivity", StickSensitivity));
            GyroSensitivityX = Clamp(save.GetVar("gyroSensitivityX", GyroSensitivityX));
            GyroSensitivityY = Clamp(save.GetVar("gyroSensitivityY", GyroSensitivityY));
            GyroEnabled = save.GetVar("gyroEnabled", GyroEnabled);
            MusicVolume = Mathf.Clamp01(save.GetVar("musicVolume", MusicVolume));
            SfxVolume = Mathf.Clamp01(save.GetVar("sfxVolume", SfxVolume));
        }
        Loaded = true;
        Apply();
    }

    public static async Task SaveNow()
    {
        var save = new Save();
        save.SetVar("mouseSensitivity", MouseSensitivity);
        save.SetVar("stickSensitivity", StickSensitivity);
        save.SetVar("gyroSensitivityX", GyroSensitivityX);
        save.SetVar("gyroSensitivityY", GyroSensitivityY);
        save.SetVar("gyroEnabled", GyroEnabled);
        save.SetVar("musicVolume", MusicVolume);
        save.SetVar("sfxVolume", SfxVolume);
        try { await Files.SaveFile(FileName, save); }
        catch (Exception e) { Debug.LogWarning($"[Settings] couldn't save {FileName}: {e.Message}"); }
    }

    public static bool DeleteFile() => Files.DeleteFile(FileName); // tests

    public static float Clamp(float sensitivity) => Mathf.Clamp(sensitivity, MinSensitivity, MaxSensitivity);
}

using Microsoft.Win32;

namespace openXRTK_Graph;

/// <summary>Optional audible feedback when a recording starts or stops.</summary>
public static class RecordingIndicator
{
    private const string SoundValue = "RecordingSound";

    public static bool SoundEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(global::XrPerf.Contracts.ControlProtocol.AppRegistryKey);
                return key?.GetValue(SoundValue) is int v ? v != 0 : true;
            }
            catch (Exception) { return true; }
        }
        set
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(global::XrPerf.Contracts.ControlProtocol.AppRegistryKey);
                key.SetValue(SoundValue, value ? 1 : 0, RegistryValueKind.DWord);
            }
            catch (Exception) { }
        }
    }

    public static void Play(bool started)
    {
        if (!SoundEnabled) return;
        Task.Run(() =>
        {
            try
            {
                if (started) { Console.Beep(880, 120); Console.Beep(1320, 180); }
                else { Console.Beep(1320, 120); Console.Beep(660, 250); }
            }
            catch (Exception) { }
        });
    }
}

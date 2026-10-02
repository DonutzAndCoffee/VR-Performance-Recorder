using Microsoft.Win32;

namespace openXRTK_Graph;

/// <summary>Settings for the in-headset overlay rendered by the XrPerf OpenXR layer (read live by the layer).</summary>
public static class OverlaySettings
{
    public const string KeyPath = global::XrPerf.Contracts.ControlProtocol.AppRegistryKey + @"\Overlay";

    /// <summary>Set by the app while a recording runs; used by the layer for "show while recording".</summary>
    public const string RecordingActive = "RecordingActive";

    /// <summary>App-side: record a process log alongside every VR session recording.</summary>
    public const string ProcessLogging = "ProcessLogging";
    public const string ProcessLogIntervalMs = "ProcessLogIntervalMs";

    public static int Get(string name, int defaultValue)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
            return key?.GetValue(name) is int v ? v : defaultValue;
        }
        catch (Exception) { return defaultValue; }
    }

    public static bool Get(string name, bool defaultValue) => Get(name, defaultValue ? 1 : 0) != 0;

    public static void Set(string name, int value)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
            key.SetValue(name, value, RegistryValueKind.DWord);
        }
        catch (Exception) { }
    }

    public static void Set(string name, bool value) => Set(name, value ? 1 : 0);
}

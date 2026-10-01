using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace openXRTK_Graph.XrPerf;

/// <summary>
/// Registers/unregisters the XrPerf implicit OpenXR API layer. Reading the state needs no
/// elevation; changes are applied via an elevated reg.exe (UAC prompt).
/// </summary>
public static class LayerManager
{
    private const string ImplicitKey = @"SOFTWARE\Khronos\OpenXR\1\ApiLayers\Implicit";
    private const string ManifestFileName = "XrPerfLayer.json";

    public enum LayerState { NotInstalled, Enabled, Disabled, Missing }

    /// <summary>Manifest shipped next to the application.</summary>
    public static string BundledManifestPath => Path.Combine(AppContext.BaseDirectory, "XrPerfLayer", ManifestFileName);

    public static bool BundledLayerAvailable =>
        File.Exists(BundledManifestPath) &&
        File.Exists(Path.Combine(Path.GetDirectoryName(BundledManifestPath)!, "XrPerfLayer.dll"));

    public static (LayerState State, string? ManifestPath) GetState()
    {
        using var key = Registry.LocalMachine.OpenSubKey(ImplicitKey);
        if (key == null) return (LayerState.NotInstalled, null);

        foreach (var name in key.GetValueNames())
        {
            if (!string.Equals(Path.GetFileName(name), ManifestFileName, StringComparison.OrdinalIgnoreCase)) continue;
            if (!File.Exists(name)) return (LayerState.Missing, name);
            return (key.GetValue(name) is int v && v == 0 ? LayerState.Enabled : LayerState.Disabled, name);
        }
        return (LayerState.NotInstalled, null);
    }

    /// <summary>Lists all implicit layers in loader order (for display).</summary>
    public static List<(string Path, bool Enabled)> ListLayers()
    {
        using var key = Registry.LocalMachine.OpenSubKey(ImplicitKey);
        if (key == null) return [];
        return key.GetValueNames().Select(n => (n, key.GetValue(n) is int v && v == 0)).ToList();
    }

    public static bool Install() => RunElevated(BuildRemoveCommands().Append(
        $"reg add \"HKLM\\{ImplicitKey}\" /v \"{BundledManifestPath}\" /t REG_DWORD /d 0 /f"));

    public static bool Uninstall() => RunElevated(BuildRemoveCommands());

    /// <summary>Keeps the registration but toggles it (value 0 = enabled, 1 = disabled).</summary>
    public static bool SetEnabled(string manifestPath, bool enabled) => RunElevated(
        [$"reg add \"HKLM\\{ImplicitKey}\" /v \"{manifestPath}\" /t REG_DWORD /d {(enabled ? 0 : 1)} /f"]);

    private static IEnumerable<string> BuildRemoveCommands()
    {
        using var key = Registry.LocalMachine.OpenSubKey(ImplicitKey);
        if (key == null) return [];
        return key.GetValueNames()
            .Where(n => string.Equals(Path.GetFileName(n), ManifestFileName, StringComparison.OrdinalIgnoreCase))
            .Select(n => $"reg delete \"HKLM\\{ImplicitKey}\" /v \"{n}\" /f")
            .ToList();
    }

    private static bool RunElevated(IEnumerable<string> commands)
    {
        var list = commands.ToList();
        if (list.Count == 0) return true;

        var psi = new ProcessStartInfo("cmd.exe", "/c " + string.Join(" & ", list.Select(c => c + " >nul")))
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        };

        try
        {
            using var process = Process.Start(psi);
            process?.WaitForExit();
            return process?.ExitCode == 0;
        }
        catch (Win32Exception)
        {
            return false; // UAC prompt cancelled
        }
    }
}

using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace openXRTK_Graph.XrPerf;

/// <summary>
/// Copies the bundled XrPerf SimHub plugin into the SimHub installation folder.
/// </summary>
public static class SimHubPluginInstaller
{
    private static readonly string[] PluginFiles = ["XrPerf.SimHubPlugin.dll", "XrPerf.Contracts.dll"];

    public static string BundleDirectory => Path.Combine(AppContext.BaseDirectory, "SimHubPlugin");

    public static bool BundleAvailable => PluginFiles.All(f => File.Exists(Path.Combine(BundleDirectory, f)));

    public static string? FindSimHubDirectory()
    {
        var candidates = new List<string?>();
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\SimHub");
            candidates.Add(key?.GetValue("InstallDirectory") as string);
        }
        catch { }
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "SimHub"));
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "SimHub"));

        return candidates.FirstOrDefault(d => !string.IsNullOrEmpty(d) && File.Exists(Path.Combine(d, "SimHubWPF.exe")));
    }

    public static bool IsSimHubRunning() => Process.GetProcessesByName("SimHubWPF").Length > 0;

    /// <summary>True if the plugin is installed and identical to the bundled version.</summary>
    public static bool? IsUpToDate(string simHubDir)
    {
        var target = Path.Combine(simHubDir, PluginFiles[0]);
        if (!File.Exists(target)) return null;
        var bundled = Path.Combine(BundleDirectory, PluginFiles[0]);
        return !File.Exists(bundled) || File.ReadAllBytes(target).AsSpan().SequenceEqual(File.ReadAllBytes(bundled));
    }

    public static bool Install(string simHubDir)
    {
        var commands = PluginFiles.Select(f =>
            $"copy /Y \"{Path.Combine(BundleDirectory, f)}\" \"{Path.Combine(simHubDir, f)}\" >nul");

        var psi = new ProcessStartInfo("cmd.exe", "/c " + string.Join(" && ", commands))
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
            return false;
        }
    }
}

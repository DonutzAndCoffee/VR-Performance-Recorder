using Microsoft.Win32;
using System;
using System.IO;
using System.Text;
using System.Timers;

namespace openXRTK_Graph;

public sealed class LiveRegistryLogger : IDisposable
{
    private readonly System.Timers.Timer _timer;
    private readonly object _fileLock = new();
    public string LogFilePath { get; }

    public LiveRegistryLogger(int intervalMs = 1000, string? directory = null)
    {
        directory ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "openxrtk-logs");
        Directory.CreateDirectory(directory);
        LogFilePath = Path.Combine(directory, $"openxrtk_registry_log_{DateTime.Now:yyyyMMdd_HHmmss}.csv");

        // Write header
        File.WriteAllText(LogFilePath, "TimestampUtc,Running,AppKey,RecordStats,ResolutionWidth,ResolutionHeight,HmdResX,HmdResY\n", Encoding.UTF8);

        _timer = new System.Timers.Timer(intervalMs) { AutoReset = true, Enabled = false };
        _timer.Elapsed += Timer_Elapsed;
    }

    public int IntervalMs
    {
        get => (int)_timer.Interval;
        set
        {
            if (value < 50) value = 50;
            _timer.Interval = value;
        }
    }

    public void Start() => _timer.Enabled = true;

    public void Stop() => _timer.Enabled = false;

    private void Timer_Elapsed(object? sender, ElapsedEventArgs e)
    {
        try
        {
            var ts = DateTime.UtcNow.ToString("o");
            RegistryKey? root = Registry.CurrentUser.OpenSubKey("Software\\OpenXR_Toolkit");
            if (root == null)
            {
                root = RegistryHelpers.OpenOpenXrToolkitKey();
                if (root == null)
                {
                    AppendLine(ts, null, null, null, null, null, null, null);
                    return;
                }
            }
            using (root)
            {

            var running = root.GetValue("running")?.ToString()?.Trim();

            string? foundApp = null;
            string? recStats = null;
            string? resW = null;
            string? resH = null;
            string? hmdx = null;
            string? hmdy = null;
            // If running points to an app subkey, prefer it
            if (!string.IsNullOrEmpty(running))
            {
                try
                {
                    using var pref = root.OpenSubKey(running);
                    if (pref != null)
                    {
                        var rec = pref.GetValue("record_stats");
                        if (rec != null)
                        {
                            foundApp = running;
                            if (rec is int i) recStats = (i != 0) ? "1" : "0";
                            else if (rec is long l) recStats = (l != 0) ? "1" : "0";
                            else if (rec is string s && (s == "1" || s.Equals("true", StringComparison.OrdinalIgnoreCase))) recStats = "1";
                            else if (rec is string ss && int.TryParse(ss, out var v)) recStats = (v != 0) ? "1" : "0";
                            else recStats = rec.ToString();

                            var w = pref.GetValue("resolution_width");
                            var h = pref.GetValue("resolution_height");
                            var hx = pref.GetValue("HMD_res_x");
                            var hy = pref.GetValue("HMD_res_y");
                            resW = w?.ToString(); resH = h?.ToString(); hmdx = hx?.ToString(); hmdy = hy?.ToString();
                        }
                    }
                }
                catch { }
            }

            if (foundApp is null)
            {
                foreach (var name in root.GetSubKeyNames())
                {
                if (string.Equals(name, "Clients", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(name, "Classes", StringComparison.OrdinalIgnoreCase))
                    continue;

                try
                {
                    using var k = root.OpenSubKey(name);
                    if (k == null) continue;
                    var rec = k.GetValue("record_stats");
                    if (rec != null)
                    {
                        foundApp = name;
                        // tolerant conversion
                        if (rec is int i) recStats = (i != 0) ? "1" : "0";
                        else if (rec is long l) recStats = (l != 0) ? "1" : "0";
                        else if (rec is string s && (s == "1" || s.Equals("true", StringComparison.OrdinalIgnoreCase))) recStats = "1";
                        else if (rec is string ss && int.TryParse(ss, out var v)) recStats = (v != 0) ? "1" : "0";
                        else recStats = rec.ToString();

                        var w = k.GetValue("resolution_width");
                        var h = k.GetValue("resolution_height");
                        var hx = k.GetValue("HMD_res_x");
                        var hy = k.GetValue("HMD_res_y");
                        resW = w?.ToString(); resH = h?.ToString(); hmdx = hx?.ToString(); hmdy = hy?.ToString();
                        break;
                    }
                }
                catch { }
                }
            }

            AppendLine(ts, running, foundApp, recStats, resW, resH, hmdx, hmdy);
            }
        }
        catch { /* best-effort logger */ }
    }

    private void AppendLine(string timestampUtc, string? running, string? appKey, string? recordStats, string? resW, string? resH, string? hmdx, string? hmdy)
    {
        var line = string.Join(",",
            Escape(timestampUtc),
            Escape(running),
            Escape(appKey),
            Escape(recordStats),
            Escape(resW),
            Escape(resH),
            Escape(hmdx),
            Escape(hmdy));

        lock (_fileLock)
        {
            File.AppendAllText(LogFilePath, line + Environment.NewLine, Encoding.UTF8);
        }
    }

    private static string Escape(string? v)
    {
        if (v is null) return "";
        if (v.Contains(',') || v.Contains('"') || v.Contains('\n'))
            return '"' + v.Replace("\"", "\"\"") + '"';
        return v;
    }

    public void Dispose()
    {
        _timer.Elapsed -= Timer_Elapsed;
        _timer.Dispose();
    }
}

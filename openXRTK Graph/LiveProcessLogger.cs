using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Timers;

namespace openXRTK_Graph;

public sealed class LiveProcessLogger : IDisposable
{
    private readonly System.Timers.Timer _timer;
    private readonly object _fileLock = new();
    public string LogFilePath { get; }
    private readonly Dictionary<int, long> _lastCpuTime = new();
    private DateTime _lastCheck = DateTime.UtcNow;

    public LiveProcessLogger(int intervalMs = 1000, string? directory = null)
    {
        directory ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "openxrtk-logs");
        Directory.CreateDirectory(directory);
        LogFilePath = Path.Combine(directory, $"openxrtk_process_log_{DateTime.Now:yyyyMMdd_HHmmss}.csv");

        // Write header
        File.WriteAllText(LogFilePath, "TimestampUtc,ProcessName,PID,CPUPercent,WorkingSetMB,ThreadCount\n", Encoding.UTF8);

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
            var now = DateTime.UtcNow;
            var elapsed = (now - _lastCheck).TotalSeconds;
            _lastCheck = now;

            var procs = Process.GetProcesses()
                .Where(p => p.ProcessName.Length > 0)
                .OrderByDescending(p =>
                {
                    try { return p.TotalProcessorTime.TotalMilliseconds; }
                    catch { return 0; }
                })
                .Take(20) // Top 20 by CPU time
                .ToList();

            foreach (var proc in procs)
            {
                try
                {
                    long cpuMs = (long)proc.TotalProcessorTime.TotalMilliseconds;
                    double cpuPct = 0;

                    // Calculate CPU% from delta
                    if (_lastCpuTime.TryGetValue(proc.Id, out var lastCpu))
                    {
                        long delta = cpuMs - lastCpu;
                        // CPU% = (delta milliseconds / elapsed seconds / processor count) * 100
                        cpuPct = (delta / 1000.0 / elapsed) / Environment.ProcessorCount * 100;
                        cpuPct = Math.Max(0, Math.Min(100, cpuPct)); // Clamp to 0-100
                    }
                    _lastCpuTime[proc.Id] = cpuMs;

                    long ramMb = proc.WorkingSet64 / 1024 / 1024;
                    int threads = proc.Threads.Count;

                    AppendLine(ts, proc.ProcessName, proc.Id, cpuPct, ramMb, threads);
                }
                catch { /* Skip if process exited */ }
            }
        }
        catch { /* best-effort logger */ }
    }

    private void AppendLine(string timestampUtc, string processName, int pid, double cpuPercent, long workingSetMb, int threadCount)
    {
        var line = string.Join(",",
            Escape(timestampUtc),
            Escape(processName),
            pid,
            cpuPercent.ToString("F2"),
            workingSetMb,
            threadCount);

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
        _lastCpuTime.Clear();
    }
}

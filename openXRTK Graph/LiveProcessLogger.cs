using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Timers;

namespace openXRTK_Graph;

public sealed class LiveProcessLogger : IDisposable
{
    private readonly System.Timers.Timer _timer;
    private readonly object _fileLock = new();
    public string LogFilePath { get; }
    private readonly Dictionary<int, long> _lastCpuTime = new();
    private DateTime _lastCheck = DateTime.UtcNow;
    private int _busy;

    public LiveProcessLogger(int intervalMs = 1000, string? directory = null, string? filePath = null)
    {
        directory ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "openxrtk-logs");
        LogFilePath = filePath ?? Path.Combine(directory, $"openxrtk_process_log_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
        Directory.CreateDirectory(Path.GetDirectoryName(LogFilePath)!);

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
        // Skip tick if previous one is still running (avoid overlapping work)
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        var procs = Array.Empty<Process>();
        try
        {
            var now = DateTime.UtcNow;
            var ts = now.ToString("o");
            var elapsed = (now - _lastCheck).TotalSeconds;
            _lastCheck = now;
            if (elapsed <= 0) return;

            procs = Process.GetProcesses();
            var samples = new List<(string Name, int Pid, double Cpu, long RamMb, int Threads)>(procs.Length);
            var seen = new HashSet<int>();

            foreach (var proc in procs)
            {
                try
                {
                    int pid = proc.Id;
                    long cpuMs = (long)proc.TotalProcessorTime.TotalMilliseconds;
                    seen.Add(pid);
                    double cpuPct = 0;
                    if (_lastCpuTime.TryGetValue(pid, out var lastCpu))
                    {
                        cpuPct = ((cpuMs - lastCpu) / 1000.0 / elapsed) / Environment.ProcessorCount * 100;
                        cpuPct = Math.Max(0, Math.Min(100, cpuPct));
                    }
                    _lastCpuTime[pid] = cpuMs;

                    samples.Add((proc.ProcessName, pid, cpuPct, proc.WorkingSet64 / 1024 / 1024, proc.Threads.Count));
                }
                catch { /* access denied or process exited */ }
            }

            // Drop stale PIDs
            foreach (var pid in _lastCpuTime.Keys.Where(k => !seen.Contains(k)).ToList())
                _lastCpuTime.Remove(pid);

            var sb = new StringBuilder();
            foreach (var s in samples.OrderByDescending(s => s.Cpu).ThenByDescending(s => s.RamMb).Take(20))
            {
                sb.Append(Escape(ts)).Append(',')
                  .Append(Escape(s.Name)).Append(',')
                  .Append(s.Pid).Append(',')
                  .Append(s.Cpu.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)).Append(',')
                  .Append(s.RamMb).Append(',')
                  .Append(s.Threads).Append(Environment.NewLine);
            }

            lock (_fileLock)
            {
                File.AppendAllText(LogFilePath, sb.ToString(), Encoding.UTF8);
            }
        }
        catch { /* best-effort logger */ }
        finally
        {
            foreach (var p in procs) p.Dispose();
            Interlocked.Exchange(ref _busy, 0);
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

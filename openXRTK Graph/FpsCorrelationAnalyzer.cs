using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace openXRTK_Graph;

/// <summary>
/// Correlates FPS drops with process activity to identify culprits.
/// </summary>
public sealed class FpsCorrelationAnalyzer
{
    public class ProcessSnapshot
    {
        public DateTime Timestamp { get; set; }
        public string ProcessName { get; set; } = string.Empty;
        public int Pid { get; set; }
        public double CpuPercent { get; set; }
        public long WorkingSetMb { get; set; }
        public int ThreadCount { get; set; }
    }

    public class FpsDropEvent
    {
        public int Index { get; set; }
        public DateTime Timestamp { get; set; }
        public double Fps { get; set; }
        public double TargetFps { get; set; }
        public double DeficitFps { get; set; }
        public List<ProcessSnapshot> SuspiciousProcesses { get; set; } = [];
    }

    /// <summary>
    /// Load process snapshots from a live process log CSV.
    /// Format: TimestampUtc,ProcessName,PID,CPUPercent,WorkingSetMB,ThreadCount
    /// </summary>
    public static List<ProcessSnapshot> LoadProcessLog(string filePath)
    {
        var snapshots = new List<ProcessSnapshot>();
        if (!File.Exists(filePath)) return snapshots;

        var lines = File.ReadAllLines(filePath);
        foreach (var line in lines.Skip(1)) // Skip header
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var parts = line.Split(',');
            if (parts.Length < 6) continue;

            if (DateTime.TryParse(parts[0], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var ts))
            {
                snapshots.Add(new ProcessSnapshot
                {
                    Timestamp = ts,
                    ProcessName = parts[1],
                    Pid = int.TryParse(parts[2], out var pid) ? pid : 0,
                    CpuPercent = double.TryParse(parts[3], CultureInfo.InvariantCulture, out var cpu) ? cpu : 0,
                    WorkingSetMb = long.TryParse(parts[4], out var ram) ? ram : 0,
                    ThreadCount = int.TryParse(parts[5], out var tc) ? tc : 0,
                });
            }
        }
        return snapshots;
    }

    /// <summary>
    /// Find FPS drops and correlate with process activity.
    /// </summary>
    public static List<FpsDropEvent> AnalyzeFpsDrops(
        IList<CsvDataPoint> fpsData,
        List<ProcessSnapshot> processLog,
        double targetFps = 90,
        double dropThresholdPct = 0.98,
        int timeWindowSeconds = 2,
        double minCpuPercent = 0.5)
    {
        var drops = new List<FpsDropEvent>();
        var threshold = targetFps * dropThresholdPct;

        for (int i = 0; i < fpsData.Count; i++)
        {
            var point = fpsData[i];
            if (point.Fps < threshold)
            {
                var ts = point.Time;
                var start = ts.AddSeconds(-timeWindowSeconds);
                var end = ts.AddSeconds(timeWindowSeconds);

                // Find processes active in the time window
                var suspiciousProcs = processLog
                    .Where(p => p.Timestamp >= start && p.Timestamp <= end && p.CpuPercent > minCpuPercent)
                    .GroupBy(p => p.ProcessName)
                    .Select(g => g.OrderByDescending(p => p.CpuPercent).First())
                    .OrderByDescending(p => p.CpuPercent)
                    .ToList();

                drops.Add(new FpsDropEvent
                {
                    Index = i,
                    Timestamp = ts,
                    Fps = point.Fps,
                    TargetFps = targetFps,
                    DeficitFps = targetFps - point.Fps,
                    SuspiciousProcesses = suspiciousProcs,
                });
            }
        }

        return drops;
    }

    /// <summary>
    /// Format FPS drops with process attribution for display.
    /// </summary>
    public static string FormatFpsDropAnalysis(List<FpsDropEvent> drops, int maxDropsToShow = 20)
    {
        if (drops.Count == 0)
            return "✅ No significant FPS drops detected!";

        var sb = new StringBuilder();
        sb.AppendLine($"⚠️  FPS Drop Attribution ({drops.Count} events)");
        sb.AppendLine();

        var topDrops = drops.OrderByDescending(d => d.DeficitFps).Take(maxDropsToShow);

        foreach (var drop in topDrops)
        {
            sb.AppendLine($"  [{drop.Timestamp:HH:mm:ss.fff}] FPS {drop.Fps:F1} (target: {drop.TargetFps:F0}, deficit: {drop.DeficitFps:F1})");

            if (drop.SuspiciousProcesses.Count == 0)
            {
                sb.AppendLine("    └─ No significant process activity detected");
            }
            else
            {
                foreach (var proc in drop.SuspiciousProcesses.Take(5))
                {
                    sb.AppendLine($"    ├─ {proc.ProcessName} (PID {proc.Pid}): {proc.CpuPercent:F1}% CPU, {proc.WorkingSetMb} MB RAM");
                }
                if (drop.SuspiciousProcesses.Count > 5)
                    sb.AppendLine($"    └─ ... and {drop.SuspiciousProcesses.Count - 5} more");
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }

    /// <summary>
    /// Generate a detailed CSV of FPS drops with process info for further analysis.
    /// </summary>
    public static void ExportDropAnalysis(
        List<FpsDropEvent> drops,
        string outputPath)
    {
        using (var writer = new StreamWriter(outputPath, false, Encoding.UTF8))
        {
            writer.WriteLine("Timestamp,FPS,TargetFPS,DeficitFPS,TopProcesses");

            foreach (var drop in drops)
            {
                var procInfo = string.Join(" | ",
                    drop.SuspiciousProcesses
                        .Take(3)
                        .Select(p => $"{p.ProcessName}({p.CpuPercent:F1}%)"));

                writer.WriteLine($"{drop.Timestamp:o},{drop.Fps:F2},{drop.TargetFps:F1},{drop.DeficitFps:F2},\"{procInfo}\"");
            }
        }
    }
}

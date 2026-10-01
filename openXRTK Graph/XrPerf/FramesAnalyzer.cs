using System.Globalization;
using System.IO;
using System.Text;

namespace openXRTK_Graph.XrPerf;

/// <summary>
/// Evaluates a per-frame "_frames.csv" and writes a "_frames_summary.csv" with
/// percentiles, missed-frame counts and spike attribution (overall and per lap).
/// </summary>
public static class FramesAnalyzer
{
    public const string SummarySuffix = "_frames_summary.csv";

    private readonly record struct Frame(double FrameTimeMs, double AppCpuUs, double RenderCpuUs, double AppGpuUs, bool GpuValid, int Lap);

    public static string GetSummaryPath(string framesCsvPath)
    {
        string name = Path.GetFileName(framesCsvPath);
        string baseName = name.EndsWith(XrPerfRecorder.FramesSuffix, StringComparison.OrdinalIgnoreCase)
            ? name[..^XrPerfRecorder.FramesSuffix.Length]
            : Path.GetFileNameWithoutExtension(name);
        return Path.Combine(Path.GetDirectoryName(framesCsvPath)!, baseName + SummarySuffix);
    }

    /// <summary>Analyzes the file and writes the summary next to it. Returns the summary path.</summary>
    public static string Analyze(string framesCsvPath, double refreshRateHz)
    {
        string path = GetSummaryPath(framesCsvPath);
        File.WriteAllText(path, BuildCsv(framesCsvPath, refreshRateHz), new UTF8Encoding(false));
        return path;
    }

    /// <summary>Builds a human-readable table (metrics as rows, overall/laps as columns).</summary>
    public static string BuildReport(string framesCsvPath, double refreshRateHz)
    {
        var rows = BuildCsv(framesCsvPath, refreshRateHz)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.TrimEnd('\r').Split(','))
            .ToList();
        if (rows.Count < 2) return "No valid frames found.";

        int labelWidth = rows[0].Max(h => h.Length) + 2;
        var sb = new StringBuilder();
        for (int col = 0; col < rows[0].Length; col++)
        {
            sb.Append(rows[0][col].PadRight(labelWidth));
            for (int r = 1; r < rows.Count; r++)
                sb.Append((col < rows[r].Length ? rows[r][col] : "").PadLeft(12));
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private static string BuildCsv(string framesCsvPath, double refreshRateHz)
    {
        var frames = Read(framesCsvPath);
        double budgetMs = refreshRateHz > 0 ? 1000.0 / refreshRateHz : 0;

        var c = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.AppendLine("scope,frames,avg frametime (ms),p50 (ms),p95 (ms),p99 (ms),p99.9 (ms),max (ms),1% low fps,budget (ms),missed frames,missed (%),"
                    + "spike avg appCPU (us),spike avg renderCPU (us),spike avg appGPU (us)");

        AppendScope(sb, "all", frames, budgetMs, c);
        foreach (var lap in frames.Select(f => f.Lap).Distinct().OrderBy(l => l))
            AppendScope(sb, "lap " + lap.ToString(c), frames.Where(f => f.Lap == lap).ToList(), budgetMs, c);

        return sb.ToString();
    }

    private static readonly double[] CommonRefreshRates = [60, 72, 80, 90, 120, 144];

    /// <summary>Guesses the display refresh rate from the median frametime (snapped to common HMD rates).</summary>
    public static double EstimateRefreshRate(string framesCsvPath)
    {
        var ft = Read(framesCsvPath).Select(f => f.FrameTimeMs).OrderBy(v => v).ToArray();
        if (ft.Length == 0) return 0;
        double fps = 1000.0 / Percentile(ft, 0.5);
        return CommonRefreshRates.OrderBy(r => Math.Abs(r - fps)).First();
    }

    /// <summary>Short plain-language interpretation of the overall result.</summary>
    public static string BuildVerdict(string framesCsvPath, double refreshRateHz)
    {
        var frames = Read(framesCsvPath);
        if (frames.Count == 0 || refreshRateHz <= 0) return "";
        double budget = 1000.0 / refreshRateHz;
        var ft = frames.Select(f => f.FrameTimeMs).OrderBy(v => v).ToArray();
        double missedPct = frames.Count(f => f.FrameTimeMs > budget * 1.05) * 100.0 / ft.Length;
        double p99 = Percentile(ft, 0.99);

        var sb = new StringBuilder();
        sb.AppendLine($"Target: {refreshRateHz:F0} Hz = {budget:F2} ms per frame.");
        sb.AppendLine(missedPct < 1 && p99 < budget * 1.1
            ? $"Smooth: {100 - missedPct:F1}% of frames on time, 99% of frames below {p99:F2} ms. No stutter detected."
            : $"Stutter: {missedPct:F1}% of frames took longer than the budget (99% below {p99:F2} ms).");

        var g = frames.Where(f => f.GpuValid).ToList();
        if (g.Count > 0)
        {
            double cpu = frames.Average(f => f.AppCpuUs) / 1000.0;
            double gpu = g.Average(f => f.AppGpuUs) / 1000.0;
            sb.AppendLine($"Load: CPU {cpu:F1} ms ({cpu / budget * 100:F0}% of budget), GPU {gpu:F1} ms ({gpu / budget * 100:F0}% of budget). " +
                          (gpu > cpu ? "GPU is the limiting side." : "CPU is the limiting side."));
        }
        return sb.ToString();
    }

    /// <summary>Returns the "_frames.csv" belonging to a session CSV, or null if none exists.</summary>
    public static string? FindFramesFile(string sessionCsvPath)
    {
        if (sessionCsvPath.EndsWith(XrPerfRecorder.FramesSuffix, StringComparison.OrdinalIgnoreCase))
            return sessionCsvPath;
        string candidate = Path.Combine(Path.GetDirectoryName(sessionCsvPath)!,
            Path.GetFileNameWithoutExtension(sessionCsvPath) + XrPerfRecorder.FramesSuffix);
        return File.Exists(candidate) ? candidate : null;
    }

    private static void AppendScope(StringBuilder sb, string scope, List<Frame> frames, double budgetMs, CultureInfo c)
    {
        var ft = frames.Select(f => f.FrameTimeMs).OrderBy(v => v).ToArray();
        if (ft.Length == 0) return;

        double p99 = Percentile(ft, 0.99);
        var spikes = budgetMs > 0 ? frames.Where(f => f.FrameTimeMs > budgetMs).ToList() : new List<Frame>();
        var gpuSpikes = spikes.Where(f => f.GpuValid).ToList();

        string F(double v) => v.ToString("F3", c);
        string Avg(IEnumerable<double> v) => v.Any() ? v.Average().ToString("F0", c) : "";

        sb.AppendLine(string.Join(",",
            scope,
            ft.Length.ToString(c),
            F(ft.Average()),
            F(Percentile(ft, 0.50)),
            F(Percentile(ft, 0.95)),
            F(p99),
            F(Percentile(ft, 0.999)),
            F(ft[^1]),
            p99 > 0 ? (1000.0 / p99).ToString("F1", c) : "",
            budgetMs > 0 ? F(budgetMs) : "",
            budgetMs > 0 ? spikes.Count.ToString(c) : "",
            budgetMs > 0 ? (spikes.Count * 100.0 / ft.Length).ToString("F2", c) : "",
            Avg(spikes.Select(f => f.AppCpuUs)),
            Avg(spikes.Select(f => f.RenderCpuUs)),
            Avg(gpuSpikes.Select(f => f.AppGpuUs))));
    }

    private static double Percentile(double[] sorted, double p)
    {
        if (sorted.Length == 1) return sorted[0];
        double rank = p * (sorted.Length - 1);
        int lo = (int)Math.Floor(rank);
        int hi = Math.Min(lo + 1, sorted.Length - 1);
        return sorted[lo] + (sorted[hi] - sorted[lo]) * (rank - lo);
    }

    private static List<Frame> Read(string path)
    {
        var c = CultureInfo.InvariantCulture;
        var list = new List<Frame>();
        using var reader = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
        reader.ReadLine(); // header
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            var p = line.Split(',');
            if (p.Length < 8) continue;
            if (!double.TryParse(p[2], NumberStyles.Float, c, out double ft) || ft <= 0) continue; // first frame has no delta
            double.TryParse(p[3], NumberStyles.Float, c, out double app);
            double.TryParse(p[4], NumberStyles.Float, c, out double render);
            bool gpuValid = p[6] == "1";
            double gpu = 0;
            if (gpuValid) double.TryParse(p[5], NumberStyles.Float, c, out gpu);
            int.TryParse(p[7], NumberStyles.Integer, c, out int lap);
            list.Add(new Frame(ft, app, render, gpu, gpuValid, lap));
        }
        return list;
    }
}

using System.Text;

namespace openXRTK_Graph;

/// <summary>
/// Produces human-readable analysis texts for each chart tab.
/// </summary>
public static class AnalysisEngine
{
    // Percentiles helper
    private static double Percentile(IList<double> sorted, double p)
    {
        if (sorted.Count == 0) return 0;
        double idx = (sorted.Count - 1) * p;
        int lo = (int)idx, hi = Math.Min(lo + 1, sorted.Count - 1);
        return sorted[lo] + (idx - lo) * (sorted[hi] - sorted[lo]);
    }

    private static List<double> Sorted(IEnumerable<double> values) =>
        [.. values.OrderBy(x => x)];

    // ─── FPS Analysis ────────────────────────────────────────────────────────

    public static string AnalyzeFps(IList<CsvDataPoint> data, SessionMetadata? meta)
    {
        if (data.Count == 0) return "";
        var sb = new StringBuilder();

        var fps = Sorted(data.Select(d => d.Fps));
        double avg  = fps.Average();
        double min  = fps.Min();
        double max  = fps.Max();
        double p1   = Percentile(fps, 0.01);
        double p99  = Percentile(fps, 0.99);
        double stdDev = StdDev(fps, avg);

        int? target = meta?.TargetRate;

        sb.AppendLine("📊 FPS Analysis");
        sb.AppendLine();
        sb.AppendLine($"  Average: {avg:F1} fps   Min: {min:F1}   Max: {max:F1}");
        sb.AppendLine($"  1st percentile (1%low): {p1:F1} fps   99th: {p99:F1} fps");
        sb.AppendLine($"  Std deviation: {stdDev:F2} fps");
        sb.AppendLine();

        if (target.HasValue)
        {
            double targetFps = target.Value;
            double dropPct = fps.Count(f => f < targetFps * 0.98) / (double)fps.Count * 100;
            sb.AppendLine($"  Target rate: {target} Hz");
            if (dropPct < 1)
                sb.AppendLine($"  ✅ Framerate was stable at target — drops below target: {dropPct:F1}% of frames");
            else if (dropPct < 5)
                sb.AppendLine($"  ⚠️  Occasional drops below target: {dropPct:F1}% of frames");
            else
                sb.AppendLine($"  ❌ Frequent drops below target: {dropPct:F1}% of frames — reprojection likely active");
            sb.AppendLine();
        }

        if (stdDev < 1.0)
            sb.AppendLine("  ✅ Very stable framerate (low variance).");
        else if (stdDev < 3.0)
            sb.AppendLine("  ℹ️  Moderate framerate variation — minor stutters possible.");
        else
            sb.AppendLine("  ⚠️  High framerate variance — noticeable stutters likely.");

        AppendSessionHeader(sb, meta);
        return sb.ToString().TrimEnd();
    }

    // ─── FPS + Frametime Overlay Analysis ────────────────────────────────────

    public static string AnalyzeFpsAndFrametime(IList<CsvDataPoint> data, SessionMetadata? meta)
    {
        if (data.Count == 0) return "";
        var sb = new StringBuilder();

        var fps    = Sorted(data.Select(d => d.Fps));
        var appGpu = Sorted(data.Select(d => d.AppGpuMs));
        var appCpu = Sorted(data.Select(d => d.AppCpuMs));

        double fpsAvg  = fps.Average();
        double gpuAvg  = appGpu.Average();
        double cpuAvg  = appCpu.Average();
        int?   target  = meta?.TargetRate;
        double budgetMs = target.HasValue ? 1000.0 / target.Value : 0;

        sb.AppendLine("📊 FPS & Frametime Correlation");
        sb.AppendLine();
        sb.AppendLine($"  FPS       avg {fpsAvg:F1}  1%low {Percentile(fps, 0.01):F1}  min {fps.Min():F1}");
        sb.AppendLine($"  App GPU   avg {gpuAvg:F2} ms  p99 {Percentile(appGpu, 0.99):F2} ms");
        sb.AppendLine($"  App CPU   avg {cpuAvg:F2} ms  p99 {Percentile(appCpu, 0.99):F2} ms");
        sb.AppendLine();

        // Identify what limits FPS
        if (budgetMs > 0)
        {
            sb.AppendLine($"  Frame budget at {target} Hz: {budgetMs:F2} ms");
            double gpuOverPct = appGpu.Count(v => v > budgetMs) / (double)appGpu.Count * 100;
            double cpuOverPct = appCpu.Count(v => v > budgetMs) / (double)appCpu.Count * 100;

            if (gpuOverPct >= cpuOverPct && gpuOverPct > 2)
                sb.AppendLine($"  ⚠️  GPU is the primary limiter — exceeds budget on {gpuOverPct:F1}% of frames.");
            else if (cpuOverPct > 2)
                sb.AppendLine($"  ⚠️  CPU is the primary limiter — exceeds budget on {cpuOverPct:F1}% of frames.");
            else
                sb.AppendLine("  ✅ Both CPU and GPU stay within frame budget.");
            sb.AppendLine();
        }

        // Correlation: do high GPU frametimes coincide with low FPS?
        // Simple check: split into high-GPU and low-GPU halves, compare FPS
        double gpuMedian = Percentile(appGpu, 0.5);
        var highGpuFps = data.Where(d => d.AppGpuMs > gpuMedian).Select(d => d.Fps).ToList();
        var lowGpuFps  = data.Where(d => d.AppGpuMs <= gpuMedian).Select(d => d.Fps).ToList();
        if (highGpuFps.Count > 0 && lowGpuFps.Count > 0)
        {
            double fpsDiff = lowGpuFps.Average() - highGpuFps.Average();
            if (fpsDiff > 1.0)
                sb.AppendLine($"  ℹ️  FPS drops ~{fpsDiff:F1} when GPU frametime is above median — clear GPU pressure correlation.");
            else
                sb.AppendLine("  ✅ FPS is largely independent of GPU frametime spikes — CPU or engine-side limiting.");
        }

        // FPS variance vs frametime variance
        double fpsStdDev = StdDev(fps, fpsAvg);
        double gpuStdDev = StdDev(appGpu, gpuAvg);
        sb.AppendLine();
        sb.AppendLine($"  FPS std dev: {fpsStdDev:F2}   GPU frametime std dev: {gpuStdDev:F2} ms");
        if (fpsStdDev > 2 && gpuStdDev > 1)
            sb.AppendLine("  ⚠️  Both FPS and GPU frametime are variable — workload is inconsistent.");
        else if (fpsStdDev < 1 && gpuStdDev < 0.5)
            sb.AppendLine("  ✅ Excellent stability — both FPS and frametimes are very consistent.");

        AppendSessionHeader(sb, meta);
        return sb.ToString().TrimEnd();
    }

    // ─── Frametime Distribution Analysis ─────────────────────────────────────

    public static string AnalyzeFrametimeDistribution(IList<CsvDataPoint> data, SessionMetadata? meta)
    {
        if (data.Count == 0) return "";
        var sb = new StringBuilder();

        var appCpu  = Sorted(data.Select(d => d.AppCpuMs));
        var renCpu  = Sorted(data.Select(d => d.RenderCpuMs));
        var appGpu  = Sorted(data.Select(d => d.AppGpuMs));

        int? target = meta?.TargetRate;
        double budgetMs = target.HasValue ? 1000.0 / target.Value : 0;

        sb.AppendLine("📊 Frametime Distribution Analysis");
        sb.AppendLine();
        sb.AppendLine("  CPU (App CPU / Render CPU)");
        sb.AppendLine($"    App CPU   — avg: {appCpu.Average():F2} ms  " +
                      $"p99: {Percentile(appCpu, 0.99):F2} ms  max: {appCpu.Max():F2} ms");
        sb.AppendLine($"    Render CPU — avg: {renCpu.Average():F2} ms  " +
                      $"p99: {Percentile(renCpu, 0.99):F2} ms  max: {renCpu.Max():F2} ms");
        sb.AppendLine();
        sb.AppendLine("  GPU (App GPU)");
        sb.AppendLine($"    App GPU   — avg: {appGpu.Average():F2} ms  " +
                      $"p99: {Percentile(appGpu, 0.99):F2} ms  max: {appGpu.Max():F2} ms");
        sb.AppendLine();

        // Bottleneck detection
        double cpuAvg = appCpu.Average();
        double gpuAvg = appGpu.Average();
        if (gpuAvg > cpuAvg * 1.2)
            sb.AppendLine("  ⚠️  GPU-bound: GPU frametime is significantly higher than CPU frametime.");
        else if (cpuAvg > gpuAvg * 1.2)
            sb.AppendLine("  ⚠️  CPU-bound: CPU frametime is significantly higher than GPU frametime.");
        else
            sb.AppendLine("  ✅ CPU and GPU workload is balanced.");

        // Budget check
        if (budgetMs > 0)
        {
            double gpuOverBudgetPct = appGpu.Count(v => v > budgetMs) / (double)appGpu.Count * 100;
            sb.AppendLine();
            sb.AppendLine($"  Frame budget at {target} Hz: {budgetMs:F2} ms");
            if (gpuOverBudgetPct < 1)
                sb.AppendLine($"  ✅ GPU stays within frame budget on {100 - gpuOverBudgetPct:F1}% of frames.");
            else if (gpuOverBudgetPct < 10)
                sb.AppendLine($"  ⚠️  GPU exceeded frame budget on {gpuOverBudgetPct:F1}% of frames.");
            else
                sb.AppendLine($"  ❌ GPU exceeded frame budget on {gpuOverBudgetPct:F1}% of frames — reprojection likely.");
        }

        // Spike detection: p99 vs avg ratio
        double gpuSpikeRatio = Percentile(appGpu, 0.99) / Math.Max(gpuAvg, 0.001);
        if (gpuSpikeRatio > 1.5)
            sb.AppendLine($"  ⚠️  GPU spikes detected: p99 is {gpuSpikeRatio:F1}× the average — inconsistent load.");

        AppendOxrtkSettings(sb, meta);
        AppendSessionHeader(sb, meta);
        return sb.ToString().TrimEnd();
    }

    // ─── CPU/GPU Time Series Analysis ────────────────────────────────────────

    public static string AnalyzeCpuGpu(IList<CsvDataPoint> data, SessionMetadata? meta)
    {
        if (data.Count == 0) return "";
        var sb = new StringBuilder();

        var appCpu = Sorted(data.Select(d => d.AppCpuMs));
        var renCpu = Sorted(data.Select(d => d.RenderCpuMs));
        var appGpu = Sorted(data.Select(d => d.AppGpuMs));

        sb.AppendLine("📊 CPU / GPU Frametime Analysis");
        sb.AppendLine();

        sb.AppendLine($"  App CPU    avg {appCpu.Average():F2} ms  |  " +
                      $"p1%: {Percentile(appCpu, 0.01):F2}  p99%: {Percentile(appCpu, 0.99):F2}  max: {appCpu.Max():F2}");
        sb.AppendLine($"  Render CPU avg {renCpu.Average():F2} ms  |  " +
                      $"p1%: {Percentile(renCpu, 0.01):F2}  p99%: {Percentile(renCpu, 0.99):F2}  max: {renCpu.Max():F2}");
        sb.AppendLine($"  App GPU    avg {appGpu.Average():F2} ms  |  " +
                      $"p1%: {Percentile(appGpu, 0.01):F2}  p99%: {Percentile(appGpu, 0.99):F2}  max: {appGpu.Max():F2}");
        sb.AppendLine();

        // Trend: compare first and last 10% of data for drift
        int tenth = Math.Max(1, data.Count / 10);
        double gpuFirst = data.Take(tenth).Average(d => d.AppGpuMs);
        double gpuLast  = data.TakeLast(tenth).Average(d => d.AppGpuMs);
        double drift    = gpuLast - gpuFirst;
        if (Math.Abs(drift) > 1.0)
        {
            string dir = drift > 0 ? "increased" : "decreased";
            sb.AppendLine($"  ℹ️  GPU frametime {dir} by {Math.Abs(drift):F2} ms over the session " +
                          $"({gpuFirst:F2} ms → {gpuLast:F2} ms) — possible thermal or load change.");
        }
        else
        {
            sb.AppendLine("  ✅ GPU frametime stable throughout the session — no significant drift.");
        }

        // Stutter events: samples more than 2× the average GPU time
        double gpuAvg = appGpu.Average();
        int stutters = data.Count(d => d.AppGpuMs > gpuAvg * 2);
        if (stutters > 0)
            sb.AppendLine($"  ⚠️  {stutters} stutter event(s) detected (GPU frametime > 2× average).");

        AppendIRacingGraphicsSnippet(sb, meta);
        AppendSessionHeader(sb, meta);
        return sb.ToString().TrimEnd();
    }

    // ─── VRAM Analysis ───────────────────────────────────────────────────────

    public static string AnalyzeVram(IList<CsvDataPoint> data, SessionMetadata? meta)
    {
        if (data.Count == 0) return "";
        var sb = new StringBuilder();

        double avgMb  = data.Average(d => d.VramMb);
        double maxMb  = data.Max(d => d.VramMb);
        double minMb  = data.Min(d => d.VramMb);
        double avgPct = data.Average(d => d.VramPercent);
        double maxPct = data.Max(d => d.VramPercent);

        sb.AppendLine("📊 VRAM Analysis");
        sb.AppendLine();
        sb.AppendLine($"  Usage   avg: {avgMb:F0} MB  min: {minMb:F0} MB  max: {maxMb:F0} MB");
        sb.AppendLine($"  Fill    avg: {avgPct:F1}%   max: {maxPct:F1}%");
        sb.AppendLine();

        if (maxPct >= 95)
            sb.AppendLine("  ❌ VRAM nearly full (≥95%) — high risk of stuttering due to VRAM eviction.");
        else if (maxPct >= 80)
            sb.AppendLine($"  ⚠️  VRAM utilization reached {maxPct:F0}% — watch for texture streaming stutters.");
        else
            sb.AppendLine($"  ✅ VRAM headroom sufficient (max {maxPct:F0}%).");

        // Growth over session
        double vramFirst = data.Take(Math.Max(1, data.Count / 10)).Average(d => d.VramMb);
        double vramLast  = data.TakeLast(Math.Max(1, data.Count / 10)).Average(d => d.VramMb);
        double growth    = vramLast - vramFirst;
        if (growth > 200)
            sb.AppendLine($"  ⚠️  VRAM grew by ~{growth:F0} MB over the session — possible memory leak or texture streaming.");
        else
            sb.AppendLine($"  ✅ VRAM usage was stable (Δ {growth:+F0;-F0;0} MB).");

        // iRacing VidMemToUseMB setting
        var vidMemSetting = meta?.GetGameSetting("Graphics Options", "VidMemToUseMB");
        if (vidMemSetting is not null && int.TryParse(vidMemSetting, out int vidMemMb))
        {
            sb.AppendLine();
            sb.AppendLine($"  iRacing VidMemToUseMB setting: {vidMemMb} MB");
            double headroom = vidMemMb - maxMb;
            if (headroom < 500)
                sb.AppendLine($"  ⚠️  Only {headroom:F0} MB headroom to the iRacing VRAM limit — consider raising VidMemToUseMB.");
            else
                sb.AppendLine($"  ✅ {headroom:F0} MB headroom to the iRacing VRAM limit.");
        }

        AppendSessionHeader(sb, meta);
        return sb.ToString().TrimEnd();
    }

    // ─── Session Comparison ───────────────────────────────────────────────────

    public static string CompareSessionsAnalysis(
        IList<CsvDataPoint> dataA, SessionMetadata? metaA, string labelA,
        IList<CsvDataPoint> dataB, SessionMetadata? metaB, string labelB)
    {
        if (dataA.Count == 0 || dataB.Count == 0)
            return "Load both sessions to generate the comparison report.";
        var sb = new StringBuilder();

        var fpsA = Sorted(dataA.Select(d => d.Fps));
        var fpsB = Sorted(dataB.Select(d => d.Fps));
        var gpuA = Sorted(dataA.Select(d => d.AppGpuMs));
        var gpuB = Sorted(dataB.Select(d => d.AppGpuMs));
        var cpuA = Sorted(dataA.Select(d => d.AppCpuMs));
        var cpuB = Sorted(dataB.Select(d => d.AppCpuMs));

        double fpsAvgA = fpsA.Average(),  fpsAvgB = fpsB.Average();
        double fps1lA  = Percentile(fpsA, 0.01), fps1lB = Percentile(fpsB, 0.01);
        double fpsStdA = StdDev(fpsA, fpsAvgA),  fpsStdB = StdDev(fpsB, fpsAvgB);
        double gpuAvgA = gpuA.Average(),  gpuAvgB = gpuB.Average();
        double gpuP99A = Percentile(gpuA, 0.99), gpuP99B = Percentile(gpuB, 0.99);
        double cpuAvgA = cpuA.Average(),  cpuAvgB = cpuB.Average();
        double cpuP99A = Percentile(cpuA, 0.99), cpuP99B = Percentile(cpuB, 0.99);
        double vramAvgA = dataA.Average(d => d.VramMb), vramAvgB = dataB.Average(d => d.VramMb);

        // +1 = A wins, -1 = B wins, 0 = tie (0.5 % tolerance)
        static int WinH(double a, double b) => a > b * 1.005 ? 1 : b > a * 1.005 ? -1 : 0;
        static int WinL(double a, double b) => WinH(b, a);
        static string WS(int w) => w == 1 ? "✅ A" : w == -1 ? "✅ B" : "  —";

        int wFpsAvg  = WinH(fpsAvgA, fpsAvgB);
        int wFps1l   = WinH(fps1lA,  fps1lB);
        int wFpsStab = WinL(fpsStdA, fpsStdB);
        int wGpuAvg  = WinL(gpuAvgA, gpuAvgB);
        int wGpuP99  = WinL(gpuP99A, gpuP99B);
        int wCpuAvg  = WinL(cpuAvgA, cpuAvgB);
        int wCpuP99  = WinL(cpuP99A, cpuP99B);

        int[] winVec = [wFpsAvg, wFps1l, wFpsStab, wGpuAvg, wGpuP99, wCpuAvg, wCpuP99];
        int scoreA = winVec.Count(w => w == 1);
        int scoreB = winVec.Count(w => w == -1);

        const int C0 = 22, C1 = 14, C2 = 14, C3 = 12;
        string sep = "  " + new string('─', C0 + C1 + C2 + C3 + 8);

        void Row(string metric, string valA, string valB, string delta, int winner) =>
            sb.AppendLine("  " + metric.PadRight(C0) + valA.PadRight(C1) + valB.PadRight(C2) + delta.PadRight(C3) + WS(winner));

        sb.AppendLine("📊 Session Comparison Report");
        sb.AppendLine();
        sb.AppendLine($"  A  {labelA}   ({dataA.Count:N0} samples)");
        sb.AppendLine($"  B  {labelB}   ({dataB.Count:N0} samples)");
        sb.AppendLine();
        sb.AppendLine("  " + "Metric".PadRight(C0) + "Session A".PadRight(C1) + "Session B".PadRight(C2) + "Delta".PadRight(C3) + "Winner");
        sb.AppendLine(sep);

        Row("FPS avg",       $"{fpsAvgA:F1}",  $"{fpsAvgB:F1}",  $"{fpsAvgB-fpsAvgA:+0.0;-0.0;0.0}",    wFpsAvg);
        Row("FPS 1% low",    $"{fps1lA:F1}",   $"{fps1lB:F1}",   $"{fps1lB-fps1lA:+0.0;-0.0;0.0}",      wFps1l);
        Row("FPS stability", $"±{fpsStdA:F2}", $"±{fpsStdB:F2}", $"{fpsStdB-fpsStdA:+0.00;-0.00;0.00}", wFpsStab);
        Row("GPU avg (ms)",  $"{gpuAvgA:F2}",  $"{gpuAvgB:F2}",  $"{gpuAvgB-gpuAvgA:+0.00;-0.00;0.00}", wGpuAvg);
        Row("GPU p99 (ms)",  $"{gpuP99A:F2}",  $"{gpuP99B:F2}",  $"{gpuP99B-gpuP99A:+0.00;-0.00;0.00}", wGpuP99);
        Row("CPU avg (ms)",  $"{cpuAvgA:F2}",  $"{cpuAvgB:F2}",  $"{cpuAvgB-cpuAvgA:+0.00;-0.00;0.00}", wCpuAvg);
        Row("CPU p99 (ms)",  $"{cpuP99A:F2}",  $"{cpuP99B:F2}",  $"{cpuP99B-cpuP99A:+0.00;-0.00;0.00}", wCpuP99);
        Row("VRAM avg (MB)", $"{vramAvgA:F0}", $"{vramAvgB:F0}", $"{vramAvgB-vramAvgA:+0;-0;0}",        0);

        sb.AppendLine(sep);
        sb.AppendLine("  " + "Score (7 metrics)".PadRight(C0) + (scoreA + " wins").PadRight(C1) + (scoreB + " wins").PadRight(C2));
        sb.AppendLine("  " + "  * VRAM excluded".PadRight(C0) + "(depends on settings)");
        sb.AppendLine();

        if (scoreA > scoreB)
            sb.AppendLine($"  🏆 Session A is more performant ({scoreA}/7 metrics).");
        else if (scoreB > scoreA)
            sb.AppendLine($"  🏆 Session B is more performant ({scoreB}/7 metrics).");
        else
            sb.AppendLine("  ⚖️  Sessions are comparable — no clear winner.");

        if (Math.Abs(gpuAvgA - gpuAvgB) > 0.05)
        {
            double better = Math.Min(gpuAvgA, gpuAvgB), worse = Math.Max(gpuAvgA, gpuAvgB);
            string gpuWin = gpuAvgA < gpuAvgB ? "A" : "B";
            sb.AppendLine($"     GPU: Session {gpuWin} is {(worse - better) / worse * 100:F1}% faster ({better:F2} vs {worse:F2} ms avg).");
        }
        if (Math.Abs(fpsAvgA - fpsAvgB) > 0.2)
        {
            double better = Math.Max(fpsAvgA, fpsAvgB), worse = Math.Min(fpsAvgA, fpsAvgB);
            string fpsWin = fpsAvgA > fpsAvgB ? "A" : "B";
            sb.AppendLine($"     FPS: Session {fpsWin} is {(better - worse) / worse * 100:F1}% higher ({better:F1} vs {worse:F1} avg).");
        }

        AppendComparisonSettingsDiff(sb, metaA, metaB);
        return sb.ToString().TrimEnd();
    }

    private static void AppendComparisonSettingsDiff(StringBuilder sb, SessionMetadata? metaA, SessionMetadata? metaB)
    {
        if (metaA is null && metaB is null) return;

        sb.AppendLine();
        sb.AppendLine("  ─── OpenXR Toolkit Settings ──────────────────────────────────────────────────");
        sb.AppendLine("  " + "Setting".PadRight(24) + "Session A".PadRight(22) + "Session B");
        sb.AppendLine("  " + new string('─', 66));

        void OxrRow(string name, string? vA, string? vB)
        {
            string sa = vA ?? "n/a", sv = vB ?? "n/a";
            string flag = sa != sv ? "  ←" : "";
            sb.AppendLine("  " + name.PadRight(24) + sa.PadRight(22) + sv + flag);
        }

        OxrRow("Target rate (Hz)", metaA?.TargetRate?.ToString(), metaB?.TargetRate?.ToString());
        OxrRow("Upscaling",
            metaA?.Scaling.HasValue == true ? $"{metaA.ScalingTypeName} {metaA.Scaling}%" : null,
            metaB?.Scaling.HasValue == true ? $"{metaB.ScalingTypeName} {metaB.Scaling}%" : null);
        OxrRow("Sharpness",    metaA?.Sharpness?.ToString(), metaB?.Sharpness?.ToString());
        OxrRow("Render res",
            metaA?.ResolutionWidth.HasValue == true ? $"{metaA.ResolutionWidth}×{metaA.ResolutionHeight}" : null,
            metaB?.ResolutionWidth.HasValue == true ? $"{metaB.ResolutionWidth}×{metaB.ResolutionHeight}" : null);

        var gfxA = metaA?.GameSettings.GetValueOrDefault("Graphics Options");
        var gfxB = metaB?.GameSettings.GetValueOrDefault("Graphics Options");
        if (gfxA is null && gfxB is null) return;

        sb.AppendLine();
        sb.AppendLine("  ─── iRacing Graphics Settings (changed) ─────────────────────────────────────");
        sb.AppendLine("  " + "Setting".PadRight(24) + "Session A".PadRight(22) + "Session B");
        sb.AppendLine("  " + new string('─', 66));

        string[] trackedKeys = [
            "ShaderQuality", "MSAASamples", "ShadowDetail", "SSAO", "SSRLevel",
            "CarDetail", "LODPctMax", "MaxCarsToDraw", "NvReflexMode",
            "AntiAliasMethod", "ParticleDetail", "FoliageDetail",
            "DynamicShadowMaps", "VidMemToUseMB",
        ];

        bool anyDiff = false;
        foreach (var key in trackedKeys)
        {
            string vA = gfxA?.GetValueOrDefault(key) ?? "n/a";
            string vB = gfxB?.GetValueOrDefault(key) ?? "n/a";
            if (vA != vB) { sb.AppendLine("  " + key.PadRight(24) + vA.PadRight(22) + vB + "  ←"); anyDiff = true; }
        }
        if (!anyDiff) sb.AppendLine("  (No differences in tracked settings)");
    }

    // ─── Shared helpers ──────────────────────────────────────────────────────

    private static void AppendOxrtkSettings(StringBuilder sb, SessionMetadata? meta)
    {
        if (meta is null) return;
        sb.AppendLine();
        sb.AppendLine("  OpenXR Toolkit Settings:");
        if (meta.TargetRate.HasValue)
            sb.AppendLine($"    Target rate: {meta.TargetRate} Hz   " +
                          $"Frame budget: {1000.0 / meta.TargetRate.Value:F2} ms");
        if (meta.Scaling.HasValue && meta.ScalingType.HasValue)
            sb.AppendLine($"    Upscaling: {meta.ScalingTypeName} @ {meta.Scaling}%");
        if (meta.ResolutionWidth.HasValue)
            sb.AppendLine($"    Render resolution: {meta.ResolutionWidth} × {meta.ResolutionHeight}");
        if (meta.HmdResX.HasValue)
            sb.AppendLine($"    HMD native: {meta.HmdResX} × {meta.HmdResY}");
        if (meta.Sharpness.HasValue)
            sb.AppendLine($"    Sharpness: {meta.Sharpness}");
    }

    private static void AppendIRacingGraphicsSnippet(StringBuilder sb, SessionMetadata? meta)
    {
        if (meta is null || meta.GameSettings.Count == 0) return;
        var gfx = meta.GameSettings.GetValueOrDefault("Graphics Options");
        if (gfx is null) return;

        sb.AppendLine();
        sb.AppendLine("  iRacing Graphics Options (relevant):");

        void Row(string key, string label)
        {
            if (gfx.TryGetValue(key, out var v))
                sb.AppendLine($"    {label}: {v}");
        }

        Row("ShaderQuality",   "Shader quality");
        Row("MSAASamples",     "MSAA samples");
        Row("ShadowDetail",    "Shadow detail");
        Row("SSAO",            "SSAO");
        Row("SSRLevel",        "SSR");
        Row("CarDetail",       "Car detail");
        Row("LODPctMax",       "LOD% max");
        Row("MaxCarsToDraw",   "Max cars");
        Row("NvReflexMode",    "Nvidia Reflex");
    }

    private static void AppendSessionHeader(StringBuilder sb, SessionMetadata? meta)
    {
        if (meta is null || string.IsNullOrEmpty(meta.RunningApp)) return;
        sb.AppendLine();
        sb.AppendLine($"  Session: {meta.RunningApp}   Recorded: {meta.RecordedAt:yyyy-MM-dd HH:mm:ss}");
    }

    private static double StdDev(IList<double> values, double mean)
    {
        if (values.Count < 2) return 0;
        double variance = values.Sum(v => (v - mean) * (v - mean)) / (values.Count - 1);
        return Math.Sqrt(variance);
    }
}

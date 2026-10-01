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

	public static string CompareSessionsAnalysis(IList<CompareSession> sessions)
	{
		if (sessions.Count < 2 || sessions.Any(s => s.Data.Count == 0))
			return "Load at least two sessions to generate the comparison report.";
		var sb = new StringBuilder();
		int n = sessions.Count;

		var fpsAvg  = new double[n]; var fps1l  = new double[n]; var fpsStd = new double[n];
		var gpuAvg  = new double[n]; var gpuP99 = new double[n];
		var cpuAvg  = new double[n]; var cpuP99 = new double[n];
		var vramAvg = new double[n];
		for (int i = 0; i < n; i++)
		{
			var d   = sessions[i].Data;
			var fps = Sorted(d.Select(x => x.Fps));
			var gpu = Sorted(d.Select(x => x.AppGpuMs));
			var cpu = Sorted(d.Select(x => x.AppCpuMs));
			fpsAvg[i]  = fps.Average();
			fps1l[i]   = Percentile(fps, 0.01);
			fpsStd[i]  = StdDev(fps, fpsAvg[i]);
			gpuAvg[i]  = gpu.Average();
			gpuP99[i]  = Percentile(gpu, 0.99);
			cpuAvg[i]  = cpu.Average();
			cpuP99[i]  = Percentile(cpu, 0.99);
			vramAvg[i] = d.Average(x => x.VramMb);
		}

		// Returns index of the best session, or -1 if the best is within 0.5 % of the runner-up
		static int Best(double[] v, bool higherIsBetter)
		{
			var order = Enumerable.Range(0, v.Length)
				.OrderBy(i => higherIsBetter ? -v[i] : v[i]).ToArray();
			double best = v[order[0]], next = v[order[1]];
			bool clear = higherIsBetter ? best > next * 1.005 : next > best * 1.005;
			return clear ? order[0] : -1;
		}

		var keys = sessions.Select(s => s.Key).ToArray();
		string WS(int w) => w >= 0 ? "✅ " + keys[w] : "  —";

		int[] winners =
		[
			Best(fpsAvg, true), Best(fps1l, true), Best(fpsStd, false),
			Best(gpuAvg, false), Best(gpuP99, false),
			Best(cpuAvg, false), Best(cpuP99, false),
		];
		var scores = new int[n];
		foreach (var w in winners) if (w >= 0) scores[w]++;

		const int C0 = 22, C1 = 14;
		string sep = "  " + new string('─', C0 + C1 * n + 8);

		void Row(string metric, Func<int, string> val, int winner)
		{
			var line = new StringBuilder("  " + metric.PadRight(C0));
			for (int i = 0; i < n; i++) line.Append(val(i).PadRight(C1));
			line.Append(winner == int.MinValue ? "" : WS(winner));
			sb.AppendLine(line.ToString());
		}

		sb.AppendLine("📊 Session Comparison Report");
		sb.AppendLine();
		foreach (var s in sessions)
			sb.AppendLine($"  {s.Key}  {s.Label}   ({s.Data.Count:N0} samples)");
		sb.AppendLine();
		Row("Metric", i => "Session " + keys[i], int.MinValue);
		sb.Length -= Environment.NewLine.Length;
		sb.AppendLine("Best");
		sb.AppendLine(sep);

		Row("FPS avg",       i => $"{fpsAvg[i]:F1}",   winners[0]);
		Row("FPS 1% low",    i => $"{fps1l[i]:F1}",    winners[1]);
		Row("FPS stability", i => $"±{fpsStd[i]:F2}",  winners[2]);
		Row("GPU avg (ms)",  i => $"{gpuAvg[i]:F2}",   winners[3]);
		Row("GPU p99 (ms)",  i => $"{gpuP99[i]:F2}",   winners[4]);
		Row("CPU avg (ms)",  i => $"{cpuAvg[i]:F2}",   winners[5]);
		Row("CPU p99 (ms)",  i => $"{cpuP99[i]:F2}",   winners[6]);
		Row("VRAM avg (MB)", i => $"{vramAvg[i]:F0}",  -1);

		sb.AppendLine(sep);
		Row("Score (7 metrics)", i => scores[i] + " wins", int.MinValue);
		sb.AppendLine("  " + "  * VRAM excluded".PadRight(C0) + "(depends on settings)");
		sb.AppendLine();

		int top = scores.Max();
		var leaders = Enumerable.Range(0, n).Where(i => scores[i] == top).ToList();
		if (leaders.Count == 1)
			sb.AppendLine($"  🏆 Session {keys[leaders[0]]} is the most performant ({top}/7 metrics).");
		else
			sb.AppendLine("  ⚖️  Sessions are comparable — no clear winner.");

		int gBest = Array.IndexOf(gpuAvg, gpuAvg.Min()), gWorst = Array.IndexOf(gpuAvg, gpuAvg.Max());
		if (gpuAvg[gWorst] - gpuAvg[gBest] > 0.05)
			sb.AppendLine($"     GPU: Session {keys[gBest]} is {(gpuAvg[gWorst] - gpuAvg[gBest]) / gpuAvg[gWorst] * 100:F1}% faster than {keys[gWorst]} ({gpuAvg[gBest]:F2} vs {gpuAvg[gWorst]:F2} ms avg).");
		int fBest = Array.IndexOf(fpsAvg, fpsAvg.Max()), fWorst = Array.IndexOf(fpsAvg, fpsAvg.Min());
		if (fpsAvg[fBest] - fpsAvg[fWorst] > 0.2)
			sb.AppendLine($"     FPS: Session {keys[fBest]} is {(fpsAvg[fBest] - fpsAvg[fWorst]) / fpsAvg[fWorst] * 100:F1}% higher than {keys[fWorst]} ({fpsAvg[fBest]:F1} vs {fpsAvg[fWorst]:F1} avg).");

		AppendComparisonSettingsDiff(sb, sessions);
		return sb.ToString().TrimEnd();
	}

	private static void AppendComparisonSettingsDiff(StringBuilder sb, IList<CompareSession> sessions)
	{
		if (sessions.All(s => s.Meta is null)) return;
		int n = sessions.Count;
		const int CW = 22;

		void Header(string title)
		{
			sb.AppendLine();
			sb.AppendLine($"  ─── {title} " + new string('─', Math.Max(4, 60 - title.Length)));
			var h = new StringBuilder("  " + "Setting".PadRight(24));
			foreach (var s in sessions) h.Append(("Session " + s.Key).PadRight(CW));
			sb.AppendLine(h.ToString().TrimEnd());
			sb.AppendLine("  " + new string('─', 24 + CW * n));
		}

		void SettingRow(string name, IList<string?> values, bool onlyIfDiff)
		{
			var vals = values.Select(v => v ?? "n/a").ToList();
			bool diff = vals.Distinct().Count() > 1;
			if (onlyIfDiff && !diff) return;
			var line = new StringBuilder("  " + name.PadRight(24));
			for (int i = 0; i < n; i++)
				line.Append(i < n - 1 ? vals[i].PadRight(CW) : vals[i]);
			if (diff) line.Append("  ←");
			sb.AppendLine(line.ToString());
		}

		Header("OpenXR Toolkit Settings");
		SettingRow("Target rate (Hz)", sessions.Select(s => s.Meta?.TargetRate?.ToString()).ToList(), false);
		SettingRow("Upscaling", sessions.Select(s =>
			s.Meta?.Scaling.HasValue == true ? $"{s.Meta.ScalingTypeName} {s.Meta.Scaling}%" : null).ToList(), false);
		SettingRow("Sharpness", sessions.Select(s => s.Meta?.Sharpness?.ToString()).ToList(), false);
		SettingRow("Render res", sessions.Select(s =>
			s.Meta?.ResolutionWidth.HasValue == true ? $"{s.Meta.ResolutionWidth}×{s.Meta.ResolutionHeight}" : null).ToList(), false);

		var gfx = sessions.Select(s => s.Meta?.GameSettings.GetValueOrDefault("Graphics Options")).ToList();
		if (gfx.All(g => g is null)) return;

		Header("iRacing Graphics Settings (changed)");

		string[] trackedKeys = [
			"ShaderQuality", "MSAASamples", "ShadowDetail", "SSAO", "SSRLevel",
			"CarDetail", "LODPctMax", "MaxCarsToDraw", "NvReflexMode",
			"AntiAliasMethod", "ParticleDetail", "FoliageDetail",
			"DynamicShadowMaps", "VidMemToUseMB",
		];

		int before = sb.Length;
		foreach (var key in trackedKeys)
			SettingRow(key, gfx.Select(g => g?.GetValueOrDefault(key)).ToList(), true);
		if (sb.Length == before) sb.AppendLine("  (No differences in tracked settings)");
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

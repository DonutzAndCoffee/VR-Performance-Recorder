using XrPerf.Contracts;

namespace openXRTK_Graph.XrPerf;

public sealed record AggregatedRow(
    DateTimeOffset Time,
    double Fps,
    double AppCpuUs,
    double RenderCpuUs,
    double AppGpuUs,
    bool GpuValid,
    double FrameTimeAvgMs,
    double FrameTimeP99Ms,
    double Fps1PercentLow,
    int FrameCount);

/// <summary>
/// Collects frame samples and produces one aggregated row per interval (OXRTK-style per-second statistics).
/// </summary>
public sealed class FrameAggregator
{
    private readonly List<double> _frameTimesMs = new(1024);
    private double _appCpuSum, _renderCpuSum, _appGpuSum;
    private int _gpuCount, _frameCount;
    private long _lastEndQpc;

    public void Reset()
    {
        _lastEndQpc = 0;
        ClearInterval();
    }

    public void Add(in FrameSample sample, long qpcFrequency)
    {
        if (_lastEndQpc != 0 && sample.EndFrameQpc > _lastEndQpc)
        {
            _frameTimesMs.Add((sample.EndFrameQpc - _lastEndQpc) * 1000.0 / qpcFrequency);
        }
        _lastEndQpc = sample.EndFrameQpc;

        _frameCount++;
        _appCpuSum += sample.AppCpuUs;
        _renderCpuSum += sample.RenderCpuUs;
        if ((sample.Flags & FrameFlags.GpuValid) != 0)
        {
            _appGpuSum += sample.AppGpuUs;
            _gpuCount++;
        }
    }

    /// <summary>Completes the current interval. Returns null if no frames were collected.</summary>
    public AggregatedRow? Flush(DateTimeOffset time, double intervalSeconds)
    {
        if (_frameCount == 0)
        {
            return null;
        }

        double avgFrameTime = 0, p99 = 0, low1 = 0;
        if (_frameTimesMs.Count > 0)
        {
            _frameTimesMs.Sort();
            avgFrameTime = _frameTimesMs.Average();
            p99 = Percentile(_frameTimesMs, 0.99);
            low1 = p99 > 0 ? 1000.0 / p99 : 0;
        }

        var row = new AggregatedRow(
            Time: time,
            Fps: intervalSeconds > 0 ? _frameCount / intervalSeconds : 0,
            AppCpuUs: _appCpuSum / _frameCount,
            RenderCpuUs: _renderCpuSum / _frameCount,
            AppGpuUs: _gpuCount > 0 ? _appGpuSum / _gpuCount : 0,
            GpuValid: _gpuCount > 0,
            FrameTimeAvgMs: avgFrameTime,
            FrameTimeP99Ms: p99,
            Fps1PercentLow: low1,
            FrameCount: _frameCount);

        ClearInterval();
        return row;
    }

    private void ClearInterval()
    {
        _frameTimesMs.Clear();
        _appCpuSum = _renderCpuSum = _appGpuSum = 0;
        _gpuCount = _frameCount = 0;
    }

    /// <summary>Nearest-rank percentile on a sorted list.</summary>
    internal static double Percentile(List<double> sorted, double p)
    {
        if (sorted.Count == 0) return 0;
        int rank = (int)Math.Ceiling(p * sorted.Count) - 1;
        return sorted[Math.Clamp(rank, 0, sorted.Count - 1)];
    }
}

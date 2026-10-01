using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using XrPerf.Contracts;

namespace openXRTK_Graph.XrPerf;

/// <summary>
/// Background recorder: polls the layer's shared memory, samples system metrics once per second
/// and writes OXRTK-compatible CSV rows plus a companion JSON with session context.
/// </summary>
public sealed class XrPerfRecorder : IDisposable
{
    private const int DefaultRowIntervalMs = 1000;
    private const int MinRowIntervalMs = 50;

    private TimeSpan _rowInterval = TimeSpan.FromMilliseconds(DefaultRowIntervalMs);
    private TimeSpan PollInterval => _rowInterval < TimeSpan.FromMilliseconds(250) ? TimeSpan.FromMilliseconds(Math.Max(10, _rowInterval.TotalMilliseconds / 2)) : TimeSpan.FromMilliseconds(250);
    private StreamWriter? _framesCsv;
    private long _sessionStartQpc;
    private volatile bool _resetSchedule;
    private Task<SystemSample>? _sysTask;
    private DateTime _lastSysSampleUtc;
    private static readonly TimeSpan SystemSampleInterval = TimeSpan.FromSeconds(1);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly object _lock = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    private readonly ShmReader _reader = new();
    private readonly SystemSampler _sampler = new();
    private readonly FrameAggregator _aggregator = new();
    private readonly List<FrameSample> _buffer = new(1024);

    private StreamWriter? _csv;
    private SessionFile? _session;
    private string? _sessionCsvPath;
    private string? _lastSessionPath;
    private DateTime _recordingStartedUtc;
    private int _currentLap;
    private double _fpsSum;
    private int _rowCount;

    public string SessionsDirectory { get; }
    public double CurrentFps { get; private set; }
    public AggregatedRow? LastRow { get; private set; }
    public SystemSample LastSystemSample { get; private set; }
    public LayerSessionInfo? LayerInfo { get; private set; }

    /// <summary>Raised on the recorder thread once per second.</summary>
    public event Action<AggregatedRow?, SystemSample>? RowProduced;

    public XrPerfRecorder(string? sessionsDirectory = null)
    {
        SessionsDirectory = sessionsDirectory ?? DefaultSessionsDirectory;
        Directory.CreateDirectory(SessionsDirectory);
        _loop = Task.Run(() => RunAsync(_cts.Token));
    }

    public static string DefaultSessionsDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "XrPerf", "sessions");

    public bool IsRecording
    {
        get { lock (_lock) return _csv is not null; }
    }

    public RecorderStatus GetStatus()
    {
        lock (_lock)
        {
            return new RecorderStatus
            {
                IsRecording = _csv is not null,
                LayerConnected = _reader.IsConnected,
                AppName = LayerInfo?.AppName,
                RuntimeName = LayerInfo?.RuntimeName,
                CurrentFps = Math.Round(CurrentFps, 1),
                CurrentLap = _currentLap,
                RecordingSeconds = _csv is not null ? (DateTime.UtcNow - _recordingStartedUtc).TotalSeconds : 0,
                CurrentSessionPath = _sessionCsvPath,
                LastSessionPath = _lastSessionPath,
            };
        }
    }

    public string Start(string? label, IDictionary<string, string>? context, int rowIntervalMs = 0, bool rawFrames = false)
    {
        lock (_lock)
        {
            StopInternal();

            int intervalMs = rowIntervalMs <= 0 ? DefaultRowIntervalMs : Math.Max(MinRowIntervalMs, rowIntervalMs);
            _rowInterval = TimeSpan.FromMilliseconds(intervalMs);
            _resetSchedule = true;

            var now = DateTimeOffset.Now;
            string appName = SanitizeFileName(
                Path.GetFileNameWithoutExtension(LayerInfo?.AppName is { Length: > 0 } a ? a : "session"));
            string baseName = $"{appName}_{now:yyyyMMdd_HHmmss}";
            _sessionCsvPath = Path.Combine(SessionsDirectory, baseName + ".csv");

            _csv = new StreamWriter(_sessionCsvPath, append: false, new UTF8Encoding(false)) { AutoFlush = true };
            _csv.WriteLine(CsvSchema.Header);

            if (rawFrames)
            {
                _framesCsv = new StreamWriter(Path.Combine(SessionsDirectory, baseName + FramesSuffix), false, new UTF8Encoding(false));
                _framesCsv.WriteLine(FramesHeader);
                _sessionStartQpc = 0;
            }

            _recordingStartedUtc = DateTime.UtcNow;
            _currentLap = 0;
            _fpsSum = 0;
            _rowCount = 0;

            _session = new SessionFile
            {
                Label = label,
                RecordedAt = now,
                Context = context is null ? new() : new Dictionary<string, string>(context),
                Layer = LayerInfo,
                System = SystemInfo.Collect(_sampler.VramTotalMb),
                RowIntervalMs = intervalMs,
                RawFramesFile = rawFrames ? baseName + FramesSuffix : null,
            };
            WriteSessionFile();
            return _sessionCsvPath;
        }
    }

    public void Stop()
    {
        lock (_lock) StopInternal();
    }

    public void MarkLap(int lapNumber)
    {
        lock (_lock)
        {
            _currentLap = lapNumber;
            _session?.Laps.Add(new LapMarker(lapNumber, DateTimeOffset.Now));
        }
    }

    private void StopInternal()
    {
        if (_csv is null) return;

        _csv.Dispose();
        _csv = null;
        _framesCsv?.Dispose();
        _framesCsv = null;
        _rowInterval = TimeSpan.FromMilliseconds(DefaultRowIntervalMs);
        _resetSchedule = true;

        if (_session is not null)
        {
            _session.EndedAt = DateTimeOffset.Now;
            _session.DurationSeconds = (DateTime.UtcNow - _recordingStartedUtc).TotalSeconds;
            _session.AverageFps = _rowCount > 0 ? Math.Round(_fpsSum / _rowCount, 2) : 0;
            _session.Layer ??= LayerInfo;
            WriteSessionFile();
        }

        _lastSessionPath = _sessionCsvPath;
        _sessionCsvPath = null;
        _session = null;
    }

    private void WriteSessionFile()
    {
        if (_session is null || _sessionCsvPath is null) return;
        string path = GetCompanionPath(_sessionCsvPath);
        File.WriteAllText(path, JsonSerializer.Serialize(_session, JsonOptions));
    }

    public static string GetCompanionPath(string csvPath) =>
        Path.Combine(Path.GetDirectoryName(csvPath)!, Path.GetFileNameWithoutExtension(csvPath) + CsvSchema.CompanionSuffix);

    private async Task RunAsync(CancellationToken token)
    {
        var nextRow = DateTime.UtcNow + _rowInterval;
        var lastFlush = DateTime.UtcNow;

        while (!token.IsCancellationRequested)
        {
            try
            {
                PollFrames();

                var now = DateTime.UtcNow;
                if (_resetSchedule)
                {
                    _resetSchedule = false;
                    _aggregator.Reset();
                    lastFlush = now;
                    nextRow = now + _rowInterval;
                }
                if (now >= nextRow)
                {
                    double interval = (now - lastFlush).TotalSeconds;
                    lastFlush = now;
                    nextRow = now + _rowInterval;
                    ProduceRow(DateTimeOffset.Now, interval);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                // Keep the recorder alive; transient errors (file locked, process exited) are ignored.
            }

            try { await Task.Delay(PollInterval, token); }
            catch (TaskCanceledException) { break; }
        }
    }

    private void PollFrames()
    {
        bool wasConnected = _reader.IsConnected;
        if (!_reader.TryConnect())
        {
            if (wasConnected)
            {
                _aggregator.Reset();
                _sampler.TargetProcessId = 0;
            }
            return;
        }

        if (!wasConnected) _aggregator.Reset();

        var info = _reader.ReadSessionInfo();
        LayerInfo = info;
        if (info is not null) _sampler.TargetProcessId = info.ProcessId;

        _buffer.Clear();
        _reader.ReadNewSamples(_buffer);
        long freq = _reader.QpcFrequency;
        foreach (var sample in _buffer) _aggregator.Add(sample, freq);

        lock (_lock)
        {
            if (_framesCsv is null || _buffer.Count == 0) return;
            if (_sessionStartQpc == 0) _sessionStartQpc = _buffer[0].WaitFrameQpc;
            foreach (var s in _buffer) _framesCsv.WriteLine(FormatFrame(s, freq, _sessionStartQpc, _currentLap));
            _framesCsv.Flush();
        }
    }

    public const string FramesSuffix = "_frames.csv";
    public const string FramesHeader = "frame,t (ms),frametime (ms),appCPU (us),renderCPU (us),appGPU (us),gpuValid,lap";

    private long _prevWaitQpc;

    internal string FormatFrame(FrameSample s, long freq, long startQpc, int lap)
    {
        var c = CultureInfo.InvariantCulture;
        double tMs = freq > 0 ? (s.WaitFrameQpc - startQpc) * 1000.0 / freq : 0;
        double ftMs = freq > 0 && _prevWaitQpc != 0 && s.WaitFrameQpc > _prevWaitQpc ? (s.WaitFrameQpc - _prevWaitQpc) * 1000.0 / freq : 0;
        _prevWaitQpc = s.WaitFrameQpc;
        bool gpuValid = (s.Flags & FrameFlags.GpuValid) != 0;
        return string.Join(",",
            s.FrameIndex.ToString(c), tMs.ToString("F3", c), ftMs.ToString("F3", c),
            s.AppCpuUs.ToString(c), s.RenderCpuUs.ToString(c), gpuValid ? s.AppGpuUs.ToString(c) : "",
            gpuValid ? "1" : "0", lap.ToString(c));
    }

    private void ProduceRow(DateTimeOffset time, double intervalSeconds)
    {
        var row = _aggregator.Flush(time, intervalSeconds);
        var sys = GetSystemSample();
        CurrentFps = row?.Fps ?? 0;
        LastRow = row;

        lock (_lock)
        {
            if (_csv is not null)
            {
                _csv.WriteLine(FormatRow(time, row, sys, _currentLap, _rowInterval.TotalMilliseconds < 1000));
                _fpsSum += row?.Fps ?? 0;
                _rowCount++;

                if (_session is not null && _session.Layer is null && LayerInfo is not null)
                {
                    _session.Layer = LayerInfo;
                    WriteSessionFile();
                }
            }
        }

        RowProduced?.Invoke(row, sys);
    }

    /// <summary>
    /// System counters are slow (hundreds of ms) and only meaningful at ~1 Hz, so they are sampled
    /// in the background and the latest value is reused for sub-second rows.
    /// </summary>
    private SystemSample GetSystemSample()
    {
        if (_sysTask is { IsCompleted: true })
        {
            if (_sysTask.Status == TaskStatus.RanToCompletion) LastSystemSample = _sysTask.Result;
            _sysTask = null;
        }

        var now = DateTime.UtcNow;
        if (_sysTask is null && now - _lastSysSampleUtc >= SystemSampleInterval)
        {
            _lastSysSampleUtc = now;
            _sysTask = Task.Run(_sampler.Sample);
        }
        return LastSystemSample;
    }

    internal static string FormatRow(DateTimeOffset time, AggregatedRow? row, SystemSample sys, int lap, bool includeMs = false)
    {
        var c = CultureInfo.InvariantCulture;
        var sb = new StringBuilder(160);
        sb.Append(FormatTime(time, includeMs)).Append(',')
          .Append((row?.Fps ?? 0).ToString("F1", c)).Append(',')
          .Append(((long)(row?.AppCpuUs ?? 0)).ToString(c)).Append(',')
          .Append(((long)(row?.RenderCpuUs ?? 0)).ToString(c)).Append(',')
          .Append(((long)(row?.AppGpuUs ?? 0)).ToString(c)).Append(',')
          .Append(((long)sys.AppVramMb).ToString(c)).Append(',')
          .Append(((long)Math.Round(sys.AppVramPercent)).ToString(c)).Append(',')
          .Append(sys.CpuPercent.ToString("F1", c)).Append(',')
          .Append(sys.GpuPercent.ToString("F1", c)).Append(',')
          .Append(((long)sys.RamUsedMb).ToString(c)).Append(',')
          .Append(((long)sys.AppRamMb).ToString(c)).Append(',')
          .Append((row?.FrameTimeAvgMs ?? 0).ToString("F2", c)).Append(',')
          .Append((row?.FrameTimeP99Ms ?? 0).ToString("F2", c)).Append(',')
          .Append((row?.Fps1PercentLow ?? 0).ToString("F1", c)).Append(',')
          .Append(lap.ToString(c));
        return sb.ToString();
    }

    /// <summary>OXRTK format: "2026-03-27 15:46:25 +0100".</summary>
    internal static string FormatTime(DateTimeOffset time, bool includeMs = false)
    {
        var offset = time.Offset;
        char sign = offset < TimeSpan.Zero ? '-' : '+';
        offset = offset.Duration();
        return time.ToString(includeMs ? "yyyy-MM-dd HH:mm:ss.fff" : "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) +
               $" {sign}{offset.Hours:00}{offset.Minutes:00}";
    }

    private static string SanitizeFileName(string name)
    {
        foreach (char ch in Path.GetInvalidFileNameChars()) name = name.Replace(ch, '_');
        return name.Replace(' ', '_');
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _loop.Wait(TimeSpan.FromSeconds(2)); }
        catch (AggregateException) { }
        Stop();
        _reader.Dispose();
        _sampler.Dispose();
        _cts.Dispose();
    }
}

public sealed record LapMarker(int Lap, DateTimeOffset Time);

/// <summary>Content of the "_xrperf.json" companion file.</summary>
public sealed class SessionFile
{
    public int FormatVersion { get; set; } = 1;
    public string? Label { get; set; }
    public DateTimeOffset RecordedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    public double DurationSeconds { get; set; }
    public double AverageFps { get; set; }
    public Dictionary<string, string> Context { get; set; } = new();
    public LayerSessionInfo? Layer { get; set; }
    public SystemInfo? System { get; set; }
    public List<LapMarker> Laps { get; set; } = new();
    public int RowIntervalMs { get; set; } = 1000;
    public string? RawFramesFile { get; set; }
}

public sealed class SystemInfo
{
    public string? CpuName { get; set; }
    public int LogicalProcessors { get; set; }
    public string? GpuName { get; set; }
    public string? GpuDriverVersion { get; set; }
    public double VramTotalMb { get; set; }
    public string? OsVersion { get; set; }

    public static SystemInfo Collect(double vramTotalMb)
    {
        var info = new SystemInfo
        {
            LogicalProcessors = Environment.ProcessorCount,
            VramTotalMb = vramTotalMb,
            OsVersion = Environment.OSVersion.VersionString,
        };

        try
        {
            using var cpuKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            info.CpuName = (cpuKey?.GetValue("ProcessorNameString") as string)?.Trim();

            using var classKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (classKey is not null)
            {
                foreach (var sub in classKey.GetSubKeyNames().Where(n => n.All(char.IsDigit)))
                {
                    using var adapter = classKey.OpenSubKey(sub);
                    if (adapter?.GetValue("DriverDesc") is not string desc) continue;
                    // Prefer the discrete adapter (the one with the most dedicated memory comes first in practice).
                    if (info.GpuName is null || desc.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ||
                        desc.Contains("Radeon", StringComparison.OrdinalIgnoreCase))
                    {
                        info.GpuName = desc;
                        info.GpuDriverVersion = adapter.GetValue("DriverVersion") as string;
                    }
                }
            }
        }
        catch (System.Security.SecurityException) { }

        return info;
    }
}

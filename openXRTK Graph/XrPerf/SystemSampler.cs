using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace openXRTK_Graph.XrPerf;

public readonly record struct SystemSample(
    double CpuPercent,
    double GpuPercent,
    double RamUsedMb,
    double AppRamMb,
    double AppCpuPercent,
    double AppVramMb,
    double VramTotalMb,
    double VramUsedMb)
{
    public double AppVramPercent => VramTotalMb > 0 ? AppVramMb / VramTotalMb * 100.0 : 0;
}

/// <summary>
/// Vendor-neutral system metrics (CPU, GPU engine utilization, RAM, VRAM) for a target process.
/// Call <see cref="Sample"/> about once per second from a background thread.
/// </summary>
public sealed class SystemSampler : IDisposable
{
    private static readonly TimeSpan InstanceRefreshInterval = TimeSpan.FromSeconds(10);

    private readonly PerformanceCounterCategory? _gpuEngineCategory;
    private readonly PerformanceCounterCategory? _gpuProcessMemoryCategory;
    private readonly PerformanceCounterCategory? _gpuAdapterMemoryCategory;

    private readonly Dictionary<string, PerformanceCounter> _engineCounters = new();
    private readonly Dictionary<string, PerformanceCounter> _processMemoryCounters = new();
    private readonly Dictionary<string, PerformanceCounter> _adapterMemoryCounters = new();
    private DateTime _lastInstanceRefresh = DateTime.MinValue;

    private long _lastIdle, _lastKernel, _lastUser;
    private TimeSpan _lastAppCpu;
    private DateTime _lastAppSample;
    private int _targetPid;

    public double VramTotalMb { get; }

    public SystemSampler()
    {
        _gpuEngineCategory = TryGetCategory("GPU Engine");
        _gpuProcessMemoryCategory = TryGetCategory("GPU Process Memory");
        _gpuAdapterMemoryCategory = TryGetCategory("GPU Adapter Memory");
        VramTotalMb = ReadDedicatedVideoMemoryMb();
        GetSystemTimes(out _lastIdle, out _lastKernel, out _lastUser);
    }

    public int TargetProcessId
    {
        get => _targetPid;
        set
        {
            if (_targetPid == value) return;
            _targetPid = value;
            _lastAppCpu = TimeSpan.Zero;
            _lastAppSample = DateTime.MinValue;
            _lastInstanceRefresh = DateTime.MinValue;
        }
    }

    public SystemSample Sample()
    {
        RefreshInstancesIfNeeded();

        return new SystemSample(
            CpuPercent: SampleSystemCpu(),
            GpuPercent: SampleGpuUtilization(),
            RamUsedMb: SampleSystemRamUsedMb(),
            AppRamMb: SampleAppRam(out double appCpu),
            AppCpuPercent: appCpu,
            AppVramMb: SumCounters(_processMemoryCounters) / (1024.0 * 1024.0),
            VramTotalMb: VramTotalMb,
            VramUsedMb: SumCounters(_adapterMemoryCounters) / (1024.0 * 1024.0));
    }

    private double SampleSystemCpu()
    {
        if (!GetSystemTimes(out long idle, out long kernel, out long user)) return 0;

        long idleDelta = idle - _lastIdle;
        long totalDelta = (kernel - _lastKernel) + (user - _lastUser); // kernel time includes idle
        _lastIdle = idle;
        _lastKernel = kernel;
        _lastUser = user;

        return totalDelta > 0 ? Math.Clamp((1.0 - (double)idleDelta / totalDelta) * 100.0, 0, 100) : 0;
    }

    private double SampleAppRam(out double appCpuPercent)
    {
        appCpuPercent = 0;
        if (_targetPid == 0) return 0;

        try
        {
            using var process = Process.GetProcessById(_targetPid);
            var now = DateTime.UtcNow;
            var cpu = process.TotalProcessorTime;
            if (_lastAppSample != DateTime.MinValue)
            {
                double elapsedMs = (now - _lastAppSample).TotalMilliseconds;
                if (elapsedMs > 0)
                {
                    appCpuPercent = Math.Clamp(
                        (cpu - _lastAppCpu).TotalMilliseconds / elapsedMs / Environment.ProcessorCount * 100.0, 0, 100);
                }
            }
            _lastAppCpu = cpu;
            _lastAppSample = now;
            return process.WorkingSet64 / (1024.0 * 1024.0);
        }
        catch (ArgumentException)
        {
            return 0; // process exited
        }
        catch (InvalidOperationException)
        {
            return 0;
        }
    }

    /// <summary>
    /// Task-Manager style: utilization summed per engine type across all processes, maximum over engine types.
    /// </summary>
    private double SampleGpuUtilization()
    {
        var perEngineType = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var (instance, counter) in _engineCounters)
        {
            float value;
            try { value = counter.NextValue(); }
            catch (InvalidOperationException) { continue; }

            int idx = instance.LastIndexOf("engtype_", StringComparison.OrdinalIgnoreCase);
            string engineType = idx >= 0 ? instance[(idx + 8)..] : instance;
            perEngineType[engineType] = perEngineType.GetValueOrDefault(engineType) + value;
        }
        return perEngineType.Count > 0 ? Math.Clamp(perEngineType.Values.Max(), 0, 100) : 0;
    }

    private static double SampleSystemRamUsedMb()
    {
        var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        return GlobalMemoryStatusEx(ref status)
            ? (status.ullTotalPhys - status.ullAvailPhys) / (1024.0 * 1024.0)
            : 0;
    }

    private void RefreshInstancesIfNeeded()
    {
        if (DateTime.UtcNow - _lastInstanceRefresh < InstanceRefreshInterval) return;
        _lastInstanceRefresh = DateTime.UtcNow;

        SyncCounters(_engineCounters, _gpuEngineCategory, "Utilization Percentage", _ => true);

        string pidPrefix = $"pid_{_targetPid}_";
        SyncCounters(_processMemoryCounters, _gpuProcessMemoryCategory, "Dedicated Usage",
            name => _targetPid != 0 && name.StartsWith(pidPrefix, StringComparison.OrdinalIgnoreCase));

        SyncCounters(_adapterMemoryCounters, _gpuAdapterMemoryCategory, "Dedicated Usage", _ => true);
    }

    private static void SyncCounters(Dictionary<string, PerformanceCounter> counters, PerformanceCounterCategory? category,
        string counterName, Func<string, bool> filter)
    {
        if (category is null) return;

        string[] instances;
        try { instances = category.GetInstanceNames(); }
        catch (InvalidOperationException) { return; }

        var wanted = new HashSet<string>(instances.Where(filter), StringComparer.OrdinalIgnoreCase);

        foreach (var stale in counters.Keys.Where(k => !wanted.Contains(k)).ToList())
        {
            counters[stale].Dispose();
            counters.Remove(stale);
        }

        foreach (var instance in wanted)
        {
            if (counters.ContainsKey(instance)) continue;
            try
            {
                var counter = new PerformanceCounter(category.CategoryName, counterName, instance, readOnly: true);
                counter.NextValue(); // prime rate counters
                counters[instance] = counter;
            }
            catch (InvalidOperationException) { /* instance vanished */ }
        }
    }

    private static double SumCounters(Dictionary<string, PerformanceCounter> counters)
    {
        double sum = 0;
        foreach (var counter in counters.Values)
        {
            try { sum += counter.RawValue; }
            catch (InvalidOperationException) { }
        }
        return sum;
    }

    private static PerformanceCounterCategory? TryGetCategory(string name)
    {
        try
        {
            return PerformanceCounterCategory.Exists(name) ? new PerformanceCounterCategory(name) : null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Largest dedicated memory of all display adapters (works for NVIDIA and AMD).</summary>
    private static double ReadDedicatedVideoMemoryMb()
    {
        const string displayClass = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";
        double max = 0;
        try
        {
            using var classKey = Registry.LocalMachine.OpenSubKey(displayClass);
            if (classKey is null) return 0;

            foreach (var sub in classKey.GetSubKeyNames().Where(n => n.All(char.IsDigit)))
            {
                try
                {
                    using var adapterKey = classKey.OpenSubKey(sub);
                    object? value = adapterKey?.GetValue("HardwareInformation.qwMemorySize");
                    long bytes = value switch
                    {
                        long l => l,
                        byte[] b when b.Length >= 8 => BitConverter.ToInt64(b, 0),
                        byte[] b when b.Length >= 4 => BitConverter.ToUInt32(b, 0),
                        int i => (uint)i,
                        _ => 0,
                    };
                    max = Math.Max(max, bytes / (1024.0 * 1024.0));
                }
                catch (System.Security.SecurityException) { }
            }
        }
        catch (System.Security.SecurityException) { }
        return max;
    }

    public void Dispose()
    {
        foreach (var counter in _engineCounters.Values.Concat(_processMemoryCounters.Values).Concat(_adapterMemoryCounters.Values))
        {
            counter.Dispose();
        }
        _engineCounters.Clear();
        _processMemoryCounters.Clear();
        _adapterMemoryCounters.Clear();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out long idleTime, out long kernelTime, out long userTime);
}

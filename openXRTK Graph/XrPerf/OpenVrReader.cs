using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using XrPerf.Contracts;

namespace openXRTK_Graph.XrPerf;

/// <summary>
/// Reads per-frame compositor timings from SteamVR (OpenVR) as a background client.
/// Used for native OpenVR titles (e.g. Assetto Corsa, AMS2) that bypass the OpenXR layer.
/// Not thread-safe; use from one thread.
/// </summary>
public sealed class OpenVrReader : IDisposable
{
    // Synthetic QPC frequency: OpenVR timestamps are seconds (double), converted to 100 ns ticks.
    public const long QpcFrequency = 10_000_000;

    // Function table layout taken from Valve's openvr_api.cs (IVRCompositor_029).
    private const string CompositorVersion = "FnTable:IVRCompositor_029";
    private const int FnGetFrameTimings = 11;
    private const int FnGetCurrentSceneFocusProcess = 24;
    private const int VRApplication_Background = 3;
    private const int MaxFramesPerPoll = 128;
    private static readonly TimeSpan ReconnectInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(5);

    private static bool _resolverRegistered;
    private static bool _libraryMissing;

    private GetFrameTimingsFn? _getFrameTimings;
    private GetCurrentSceneFocusProcessFn? _getFocusProcess;
    private Compositor_FrameTiming[] _timings = new Compositor_FrameTiming[MaxFramesPerPoll];
    private DateTime _nextConnectAttemptUtc;
    private DateTime _lastNewFrameUtc;
    private uint _lastFrameIndex;
    private uint _focusPid;
    private LayerSessionInfo? _info;

    public bool IsConnected => _getFrameTimings is not null;

    public bool TryConnect()
    {
        if (IsConnected)
        {
            if (DateTime.UtcNow - _lastNewFrameUtc > StallTimeout && !IsSteamVrRunning())
            {
                Disconnect();
                return false;
            }
            return true;
        }

        var now = DateTime.UtcNow;
        if (_libraryMissing || now < _nextConnectAttemptUtc) return false;
        _nextConnectAttemptUtc = now + ReconnectInterval;

        // Never launch SteamVR ourselves; only attach when it is already running.
        if (!IsSteamVrRunning()) return false;

        try
        {
            RegisterResolver();
            int error = 0;
            Native.VR_InitInternal2(ref error, VRApplication_Background, null);
            if (error != 0) return false;

            IntPtr table = Native.VR_GetGenericInterface(CompositorVersion, ref error);
            if (error != 0 || table == IntPtr.Zero)
            {
                Native.VR_ShutdownInternal();
                return false;
            }

            _getFrameTimings = Marshal.GetDelegateForFunctionPointer<GetFrameTimingsFn>(
                Marshal.ReadIntPtr(table, FnGetFrameTimings * IntPtr.Size));
            _getFocusProcess = Marshal.GetDelegateForFunctionPointer<GetCurrentSceneFocusProcessFn>(
                Marshal.ReadIntPtr(table, FnGetCurrentSceneFocusProcess * IntPtr.Size));
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            _libraryMissing = true;
            _getFrameTimings = null;
            return false;
        }

        _lastFrameIndex = 0;
        _focusPid = 0;
        _info = null;
        _lastNewFrameUtc = now;
        return true;
    }

    public void Disconnect()
    {
        if (_getFrameTimings is null) return;
        _getFrameTimings = null;
        _getFocusProcess = null;
        _info = null;
        try { Native.VR_ShutdownInternal(); } catch (DllNotFoundException) { }
    }

    public LayerSessionInfo? ReadSessionInfo()
    {
        if (_getFocusProcess is null) return null;

        uint pid = _getFocusProcess();
        if (pid == 0) return null;
        if (_info is not null && pid == _focusPid) return _info;

        _focusPid = pid;
        string appName = string.Empty;
        try
        {
            using var process = Process.GetProcessById((int)pid);
            appName = process.ProcessName + ".exe";
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { }

        _info = new LayerSessionInfo
        {
            ProcessId = (int)pid,
            QpcFrequency = QpcFrequency,
            AppName = appName,
            RuntimeName = "SteamVR (OpenVR)",
        };
        return _info;
    }

    /// <summary>Appends all frames completed since the last call to <paramref name="target"/>.</summary>
    public int ReadNewSamples(List<FrameSample> target)
    {
        if (_getFrameTimings is null) return 0;

        for (int i = 0; i < _timings.Length; i++) _timings[i].m_nSize = (uint)Marshal.SizeOf<Compositor_FrameTiming>();
        uint count = _getFrameTimings(_timings, (uint)_timings.Length);

        int added = 0;
        for (int i = 0; i < count; i++)
        {
            ref var t = ref _timings[i];
            if (t.m_nFrameIndex <= _lastFrameIndex && _lastFrameIndex - t.m_nFrameIndex < 1_000_000) continue;
            _lastFrameIndex = t.m_nFrameIndex;

            long qpc = (long)(t.m_flSystemTimeInSeconds * QpcFrequency);
            double appCpuMs = Math.Max(0, t.m_flSubmitFrameMs - t.m_flNewPosesReadyMs);
            target.Add(new FrameSample
            {
                FrameIndex = t.m_nFrameIndex,
                WaitFrameQpc = qpc,
                BeginFrameQpc = qpc,
                EndFrameQpc = qpc,
                AppCpuUs = ToUs(appCpuMs),
                RenderCpuUs = ToUs(t.m_flCompositorRenderCpuMs),
                AppGpuUs = ToUs(t.m_flTotalRenderGpuMs),
                Flags = t.m_flTotalRenderGpuMs > 0 ? FrameFlags.GpuValid : FrameFlags.None,
            });
            added++;
        }

        if (added > 0) _lastNewFrameUtc = DateTime.UtcNow;
        return added;
    }

    private static uint ToUs(double ms) => ms > 0 && ms < 1_000_000 ? (uint)(ms * 1000) : 0;

    private static bool IsSteamVrRunning()
    {
        var processes = Process.GetProcessesByName("vrserver");
        foreach (var p in processes) p.Dispose();
        return processes.Length > 0;
    }

    private static void RegisterResolver()
    {
        if (_resolverRegistered) return;
        _resolverRegistered = true;
        NativeLibrary.SetDllImportResolver(typeof(OpenVrReader).Assembly, (name, assembly, path) =>
        {
            if (name != Native.Library) return IntPtr.Zero;
            if (NativeLibrary.TryLoad(name, assembly, path, out var handle)) return handle;
            foreach (var candidate in FindSteamVrLibraries())
            {
                if (NativeLibrary.TryLoad(candidate, out handle)) return handle;
            }
            return IntPtr.Zero;
        });
    }

    /// <summary>Locates openvr_api.dll in the installed SteamVR runtime (via %LOCALAPPDATA%\openvr\openvrpaths.vrpath).</summary>
    private static IEnumerable<string> FindSteamVrLibraries()
    {
        var runtimes = new List<string>();
        try
        {
            string vrpath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "openvr", "openvrpaths.vrpath");
            if (File.Exists(vrpath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(vrpath));
                if (doc.RootElement.TryGetProperty("runtime", out var arr) && arr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var e in arr.EnumerateArray())
                    {
                        if (e.GetString() is { Length: > 0 } s) runtimes.Add(s);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }

        runtimes.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam", "steamapps", "common", "SteamVR"));

        foreach (var root in runtimes)
        {
            string dll = Path.Combine(root, "bin", "win64", "openvr_api.dll");
            if (File.Exists(dll)) yield return dll;
        }
    }

    public void Dispose() => Disconnect();

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint GetFrameTimingsFn([In, Out] Compositor_FrameTiming[] timings, uint frames);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint GetCurrentSceneFocusProcessFn();

    private static class Native
    {
        public const string Library = "openvr_api";

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        public static extern uint VR_InitInternal2(ref int peError, int eApplicationType, [MarshalAs(UnmanagedType.LPStr)] string? pStartupInfo);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        public static extern void VR_ShutdownInternal();

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr VR_GetGenericInterface([MarshalAs(UnmanagedType.LPStr)] string pchInterfaceVersion, ref int peError);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TrackedDevicePose
    {
        public float M0, M1, M2, M3, M4, M5, M6, M7, M8, M9, M10, M11;
        public float VelX, VelY, VelZ;
        public float AngVelX, AngVelY, AngVelZ;
        public int TrackingResult;
        public byte PoseIsValid;
        public byte DeviceIsConnected;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Compositor_FrameTiming
    {
        public uint m_nSize;
        public uint m_nFrameIndex;
        public uint m_nNumFramePresents;
        public uint m_nNumMisPresented;
        public uint m_nNumDroppedFrames;
        public uint m_nReprojectionFlags;
        public double m_flSystemTimeInSeconds;
        public float m_flPreSubmitGpuMs;
        public float m_flPostSubmitGpuMs;
        public float m_flTotalRenderGpuMs;
        public float m_flCompositorRenderGpuMs;
        public float m_flCompositorRenderCpuMs;
        public float m_flCompositorIdleCpuMs;
        public float m_flClientFrameIntervalMs;
        public float m_flPresentCallCpuMs;
        public float m_flWaitForPresentCpuMs;
        public float m_flSubmitFrameMs;
        public float m_flWaitGetPosesCalledMs;
        public float m_flNewPosesReadyMs;
        public float m_flNewFrameReadyMs;
        public float m_flCompositorUpdateStartMs;
        public float m_flCompositorUpdateEndMs;
        public float m_flCompositorRenderStartMs;
        public TrackedDevicePose m_HmdPose;
        public uint m_nNumVSyncsReadyForUse;
        public uint m_nNumVSyncsToFirstView;
        public float m_flTransferLatencyMs;
    }
}

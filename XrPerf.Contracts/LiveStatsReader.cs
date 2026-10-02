using System;
using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Text;
using System.Threading;
using L = XrPerf.Contracts.SharedMemoryLayout;

namespace XrPerf.Contracts
{
    /// <summary>Status of the in-headset overlay as reported by the layer.</summary>
    public enum OverlayStatus : uint
    {
        Disabled = 0,
        Active = 1,
        UnsupportedGraphicsApi = 2,
        NoSwapchainFormat = 3,
        SwapchainFailed = 4,
        SwapchainImagesFailed = 5,
        SpaceFailed = 6,
        GdiFailed = 7,
        NothingSelected = 8,
    }

    /// <summary>Snapshot of the most recent frame statistics (averaged over a short window).</summary>
    public sealed class LiveStats
    {
        public bool Connected { get; set; }
        public double Fps { get; set; }
        public double FrameTimeMs { get; set; }
        public double AppCpuMs { get; set; }
        public double RenderCpuMs { get; set; }
        public double GpuMs { get; set; }
        public bool GpuValid { get; set; }
        public double RefreshRate { get; set; }
        public double FrameBudgetMs { get; set; }
        public int RenderWidth { get; set; }
        public int RenderHeight { get; set; }
        public string AppName { get; set; } = string.Empty;
        public string RuntimeName { get; set; } = string.Empty;
        public string GraphicsApi { get; set; } = string.Empty;
        public OverlayStatus OverlayStatus { get; set; }
    }

    /// <summary>
    /// Lightweight, read-only view of the layer's shared memory that computes live statistics
    /// from the most recent samples. Does not consume samples, so it can run next to the recorder.
    /// </summary>
    public sealed class LiveStatsReader : IDisposable
    {
        private MemoryMappedFile _file;
        private MemoryMappedViewAccessor _view;
        private long _lastOpenAttempt;

        public double WindowSeconds { get; set; } = 0.5;

        public LiveStats Read()
        {
            var stats = new LiveStats();
            try
            {
                if (!EnsureOpen() || _view.ReadUInt32(L.Header.Magic) != L.Magic)
                    return stats;

                long freq = _view.ReadInt64(L.Header.QpcFrequency);
                long writeIndex = _view.ReadInt64(L.Header.WriteIndex);
                Interlocked.MemoryBarrier();
                if (freq <= 0 || writeIndex <= 0)
                    return stats;

                long latestQpc = _view.ReadInt64(L.SampleOffset(writeIndex - 1) + L.Sample.EndFrameQpc);
                if (Stopwatch.GetTimestamp() - latestQpc > freq * 2)
                {
                    // Layer mapping exists but no frames are flowing (app closed or paused).
                    Close();
                    return stats;
                }

                long windowStart = latestQpc - (long)(WindowSeconds * freq);
                long oldestQpc = latestQpc;
                int count = 0, gpuCount = 0;
                double appCpu = 0, renderCpu = 0, gpu = 0;
                long maxBack = Math.Min(writeIndex, L.Capacity - 16);
                for (long i = writeIndex - 1; i >= writeIndex - maxBack; i--)
                {
                    long offset = L.SampleOffset(i);
                    long endQpc = _view.ReadInt64(offset + L.Sample.EndFrameQpc);
                    if (endQpc < windowStart || endQpc > latestQpc)
                        break;
                    oldestQpc = endQpc;
                    count++;
                    appCpu += _view.ReadUInt32(offset + L.Sample.AppCpuUs);
                    renderCpu += _view.ReadUInt32(offset + L.Sample.RenderCpuUs);
                    if ((_view.ReadUInt32(offset + L.Sample.Flags) & (uint)FrameFlags.GpuValid) != 0)
                    {
                        gpu += _view.ReadUInt32(offset + L.Sample.AppGpuUs);
                        gpuCount++;
                    }
                }

                stats.Connected = true;
                if (count > 1 && latestQpc > oldestQpc)
                {
                    double span = (double)(latestQpc - oldestQpc) / freq;
                    stats.Fps = (count - 1) / span;
                    stats.FrameTimeMs = span * 1000.0 / (count - 1);
                }
                if (count > 0)
                {
                    stats.AppCpuMs = appCpu / count / 1000.0;
                    stats.RenderCpuMs = renderCpu / count / 1000.0;
                }
                stats.GpuValid = gpuCount > 0;
                stats.GpuMs = gpuCount > 0 ? gpu / gpuCount / 1000.0 : 0;
                stats.RefreshRate = _view.ReadSingle(L.Header.DisplayRefreshRate);
                stats.FrameBudgetMs = stats.RefreshRate > 0 ? 1000.0 / stats.RefreshRate : 0;
                stats.RenderWidth = (int)_view.ReadUInt32(L.Header.SwapchainWidth);
                stats.RenderHeight = (int)_view.ReadUInt32(L.Header.SwapchainHeight);
                stats.AppName = ReadString(L.Header.AppName);
                stats.RuntimeName = ReadString(L.Header.RuntimeName);
                stats.GraphicsApi = ((GraphicsApi)_view.ReadUInt32(L.Header.GraphicsApi)).ToString();
                stats.OverlayStatus = (OverlayStatus)_view.ReadUInt32(L.Header.OverlayStatus);
            }
            catch (Exception)
            {
                Close();
                stats = new LiveStats();
            }
            return stats;
        }

        private bool EnsureOpen()
        {
            if (_view != null)
                return true;
            long now = Stopwatch.GetTimestamp();
            if (now - _lastOpenAttempt < Stopwatch.Frequency)
                return false;
            _lastOpenAttempt = now;
            try
            {
                _file = MemoryMappedFile.OpenExisting(L.MappingName, MemoryMappedFileRights.Read);
                _view = _file.CreateViewAccessor(0, L.TotalSize, MemoryMappedFileAccess.Read);
                return true;
            }
            catch (Exception)
            {
                Close();
                return false;
            }
        }

        private string ReadString(int offset)
        {
            var bytes = new byte[L.StringSlotSize];
            _view.ReadArray(offset, bytes, 0, bytes.Length);
            int len = Array.IndexOf(bytes, (byte)0);
            return Encoding.UTF8.GetString(bytes, 0, len < 0 ? bytes.Length : len);
        }

        private void Close()
        {
            _view?.Dispose();
            _file?.Dispose();
            _view = null;
            _file = null;
        }

        public void Dispose() => Close();
    }
}

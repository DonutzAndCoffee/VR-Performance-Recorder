using System.IO;
using System.IO.MemoryMappedFiles;
using System.Text;
using XrPerf.Contracts;
using L = XrPerf.Contracts.SharedMemoryLayout;

namespace openXRTK_Graph.XrPerf;

/// <summary>
/// Reads frame samples published by XrPerfLayer via shared memory. Not thread-safe; use from one thread.
/// </summary>
public sealed class ShmReader : IDisposable
{
    private MemoryMappedFile? _file;
    private MemoryMappedViewAccessor? _view;
    private long _readIndex;
    private int _processId;
    private long _sessionStartQpc;

    public bool IsConnected => _view is not null;
    public long LostSamples { get; private set; }

    /// <summary>Tries to (re)connect. Returns true if a valid layer session is available.</summary>
    public bool TryConnect()
    {
        if (_view is not null)
        {
            if (IsHeaderValid() && ReadInt32(L.Header.ProcessId) == _processId &&
                _view.ReadInt64(L.Header.SessionStartQpc) == _sessionStartQpc)
            {
                return true;
            }
            Disconnect();
        }

        try
        {
            _file = MemoryMappedFile.OpenExisting(L.MappingName, MemoryMappedFileRights.Read);
            _view = _file.CreateViewAccessor(0, L.TotalSize, MemoryMappedFileAccess.Read);
        }
        catch (FileNotFoundException)
        {
            Disconnect();
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            Disconnect();
            return false;
        }

        if (!IsHeaderValid())
        {
            Disconnect();
            return false;
        }

        _processId = ReadInt32(L.Header.ProcessId);
        _sessionStartQpc = _view.ReadInt64(L.Header.SessionStartQpc);
        _readIndex = _view.ReadInt64(L.Header.WriteIndex);
        LostSamples = 0;
        return true;
    }

    public void Disconnect()
    {
        _view?.Dispose();
        _file?.Dispose();
        _view = null;
        _file = null;
    }

    public LayerSessionInfo? ReadSessionInfo()
    {
        if (_view is null) return null;

        return new LayerSessionInfo
        {
            ProcessId = ReadInt32(L.Header.ProcessId),
            QpcFrequency = _view.ReadInt64(L.Header.QpcFrequency),
            SessionStartQpc = _view.ReadInt64(L.Header.SessionStartQpc),
            GraphicsApi = (GraphicsApi)_view.ReadUInt32(L.Header.GraphicsApi),
            SwapchainWidth = _view.ReadUInt32(L.Header.SwapchainWidth),
            SwapchainHeight = _view.ReadUInt32(L.Header.SwapchainHeight),
            SwapchainSampleCount = _view.ReadUInt32(L.Header.SwapchainSampleCount),
            SwapchainFormat = _view.ReadInt64(L.Header.SwapchainFormat),
            DisplayRefreshRate = _view.ReadSingle(L.Header.DisplayRefreshRate),
            ViewCount = _view.ReadUInt32(L.Header.ViewCount),
            FovLeft = ReadFloats(L.Header.FovLeft, 4),
            FovRight = ReadFloats(L.Header.FovRight, 4),
            SessionState = _view.ReadUInt32(L.Header.SessionState),
            AppName = ReadString(L.Header.AppName),
            EngineName = ReadString(L.Header.EngineName),
            RuntimeName = ReadString(L.Header.RuntimeName),
            SystemName = ReadString(L.Header.SystemName),
        };
    }

    /// <summary>Appends all new samples since the last call to <paramref name="target"/>.</summary>
    public int ReadNewSamples(List<FrameSample> target)
    {
        if (_view is null || !IsHeaderValid()) return 0;

        long writeIndex = _view.ReadInt64(L.Header.WriteIndex);
        Interlocked.MemoryBarrier(); // acquire: samples written before WriteIndex are visible
        if (writeIndex - _readIndex > L.Capacity)
        {
            LostSamples += writeIndex - L.Capacity - _readIndex;
            _readIndex = writeIndex - L.Capacity;
        }

        int added = 0;
        for (; _readIndex < writeIndex; _readIndex++)
        {
            long offset = L.SampleOffset(_readIndex);
            var sample = new FrameSample
            {
                FrameIndex = _view.ReadInt64(offset + L.Sample.FrameIndex),
                WaitFrameQpc = _view.ReadInt64(offset + L.Sample.WaitFrameQpc),
                BeginFrameQpc = _view.ReadInt64(offset + L.Sample.BeginFrameQpc),
                EndFrameQpc = _view.ReadInt64(offset + L.Sample.EndFrameQpc),
                PredictedDisplayTimeNs = _view.ReadInt64(offset + L.Sample.PredictedDisplayTimeNs),
                PredictedDisplayPeriodNs = _view.ReadInt64(offset + L.Sample.PredictedDisplayPeriodNs),
                AppCpuUs = _view.ReadUInt32(offset + L.Sample.AppCpuUs),
                RenderCpuUs = _view.ReadUInt32(offset + L.Sample.RenderCpuUs),
                AppGpuUs = _view.ReadUInt32(offset + L.Sample.AppGpuUs),
                Flags = (FrameFlags)_view.ReadUInt32(offset + L.Sample.Flags),
            };

            if (sample.FrameIndex != _readIndex)
            {
                LostSamples++; // overwritten while reading
                continue;
            }

            target.Add(sample);
            added++;
        }
        return added;
    }

    public long QpcFrequency => _view?.ReadInt64(L.Header.QpcFrequency) ?? 1;

    private bool IsHeaderValid() =>
        _view is not null &&
        _view.ReadUInt32(L.Header.Magic) == L.Magic &&
        _view.ReadUInt32(L.Header.Version) == L.Version;

    private int ReadInt32(long offset) => _view!.ReadInt32(offset);

    private float[] ReadFloats(long offset, int count)
    {
        var values = new float[count];
        for (int i = 0; i < count; i++) values[i] = _view!.ReadSingle(offset + i * 4);
        return values;
    }

    private string ReadString(long offset)
    {
        var bytes = new byte[L.StringSlotSize];
        _view!.ReadArray(offset, bytes, 0, bytes.Length);
        int length = Array.IndexOf(bytes, (byte)0);
        return Encoding.UTF8.GetString(bytes, 0, length < 0 ? bytes.Length : length);
    }

    public void Dispose() => Disconnect();
}

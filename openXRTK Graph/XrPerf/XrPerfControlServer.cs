using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Serialization;
using XrPerf.Contracts;

namespace openXRTK_Graph.XrPerf;

/// <summary>
/// Named-pipe server for remote control (SimHub plugin). One request/response per connection.
/// </summary>
public sealed class XrPerfControlServer : IDisposable
{
    private const int MaxMessageBytes = 64 * 1024;

    private static readonly JsonSerializerOptions SummaryJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly XrPerfRecorder _recorder;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;

    /// <summary>Raised (on a background thread) when a client asks to open a session in the UI.</summary>
    public event Action<string>? OpenSessionRequested;

    /// <summary>Raised (on a background thread) when a client asks to compare 2-3 sessions.</summary>
    public event Action<IReadOnlyList<string>>? CompareSessionsRequested;

    public XrPerfControlServer(XrPerfRecorder recorder)
    {
        _recorder = recorder;
        _loop = Task.Run(() => RunAsync(_cts.Token));
    }

    private async Task RunAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(ControlProtocol.PipeName, PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Message, PipeOptions.Asynchronous);
                await pipe.WaitForConnectionAsync(token);

                byte[] request = await ReadMessageAsync(pipe, token);
                ControlResponse response = Handle(request);
                byte[] payload = ControlProtocol.Serialize(response);
                await pipe.WriteAsync(payload, token);
                await pipe.FlushAsync(token);
                pipe.WaitForPipeDrain();
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (IOException)
            {
                // Client disconnected early - wait for the next one.
            }
        }
    }

    private static async Task<byte[]> ReadMessageAsync(NamedPipeServerStream pipe, CancellationToken token)
    {
        using var ms = new MemoryStream();
        var buffer = new byte[4096];
        do
        {
            int read = await pipe.ReadAsync(buffer, token);
            if (read == 0) break;
            ms.Write(buffer, 0, read);
            if (ms.Length > MaxMessageBytes) throw new IOException("Request too large.");
        }
        while (!pipe.IsMessageComplete);
        return ms.ToArray();
    }

    internal ControlResponse Handle(byte[] requestBytes)
    {
        ControlRequest request;
        try
        {
            request = ControlProtocol.Deserialize<ControlRequest>(requestBytes, requestBytes.Length);
        }
        catch (System.Runtime.Serialization.SerializationException ex)
        {
            return ControlResponse.Fail("Invalid request: " + ex.Message);
        }

        try
        {
            switch (request.Command)
            {
                case ControlProtocol.Commands.Status:
                    return ControlResponse.Ok(_recorder.GetStatus());

                case ControlProtocol.Commands.Start:
                    _recorder.Start(request.Label, request.Context, request.RowIntervalMs, request.RawFrames);
                    return ControlResponse.Ok(_recorder.GetStatus());

                case ControlProtocol.Commands.Stop:
                    _recorder.Stop();
                    return ControlResponse.Ok(_recorder.GetStatus());

                case ControlProtocol.Commands.Lap:
                    _recorder.MarkLap(request.LapNumber);
                    return ControlResponse.Ok(_recorder.GetStatus());

                case ControlProtocol.Commands.ListSessions:
                    return new ControlResponse
                    {
                        Success = true,
                        Sessions = ListSessions(_recorder.SessionsDirectory, request.MaxCount > 0 ? request.MaxCount : 50),
                    };

                case ControlProtocol.Commands.OpenSession:
                    if (string.IsNullOrEmpty(request.SessionPath) || !File.Exists(request.SessionPath))
                        return ControlResponse.Fail("Session file not found.");
                    OpenSessionRequested?.Invoke(request.SessionPath);
                    return ControlResponse.Ok();

                case ControlProtocol.Commands.CompareSessions:
                {
                    var paths = request.SessionPaths?.Where(p => !string.IsNullOrEmpty(p)).ToList() ?? [];
                    if (paths.Count < 2 || paths.Count > ControlProtocol.MaxCompareSessions)
                        return ControlResponse.Fail($"Select 2 to {ControlProtocol.MaxCompareSessions} sessions to compare.");
                    if (paths.FirstOrDefault(p => !File.Exists(p)) is { } missing)
                        return ControlResponse.Fail($"Session file not found: {missing}");
                    CompareSessionsRequested?.Invoke(paths);
                    return ControlResponse.Ok();
                }

                default:
                    return ControlResponse.Fail($"Unknown command '{request.Command}'.");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ControlResponse.Fail(ex.Message);
        }
    }

    public static List<SessionSummary> ListSessions(string directory, int maxCount)
    {
        if (!Directory.Exists(directory)) return new();

        return new DirectoryInfo(directory)
            .GetFiles("*.csv")
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .Take(maxCount)
            .Select(ToSummary)
            .ToList();
    }

    private static SessionSummary ToSummary(FileInfo csv)
    {
        var summary = new SessionSummary
        {
            Path = csv.FullName,
            AppName = Path.GetFileNameWithoutExtension(csv.Name),
            StartTime = csv.CreationTime,
        };

        string companion = XrPerfRecorder.GetCompanionPath(csv.FullName);
        if (!File.Exists(companion)) return summary;

        try
        {
            var session = JsonSerializer.Deserialize<SessionFile>(File.ReadAllText(companion), SummaryJsonOptions);
            if (session is null) return summary;

            summary.Label = session.Label;
            summary.AppName = session.Layer?.AppName is { Length: > 0 } app ? app : summary.AppName;
            summary.StartTime = session.RecordedAt.LocalDateTime;
            summary.DurationSeconds = session.DurationSeconds;
            summary.AverageFps = session.AverageFps;
            summary.Context = session.Context;
        }
        catch (JsonException) { }
        catch (IOException) { }

        return summary;
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _loop.Wait(TimeSpan.FromSeconds(2)); }
        catch (AggregateException) { }
        _cts.Dispose();
    }
}

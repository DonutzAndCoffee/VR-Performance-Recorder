using System;
using System.IO;
using System.IO.Pipes;
using XrPerf.Contracts;

namespace XrPerf.SimHubPlugin
{
    /// <summary>
    /// Synchronous one-request-per-connection client for the recorder's control pipe.
    /// </summary>
    public sealed class XrPerfClient
    {
        private readonly int _timeoutMs;

        public XrPerfClient(int timeoutMs = 500)
        {
            _timeoutMs = timeoutMs;
        }

        public ControlResponse Send(ControlRequest request)
        {
            try
            {
                using (var pipe = new NamedPipeClientStream(".", ControlProtocol.PipeName, PipeDirection.InOut))
                {
                    pipe.Connect(_timeoutMs);
                    pipe.ReadMode = PipeTransmissionMode.Message;

                    var payload = ControlProtocol.Serialize(request);
                    pipe.Write(payload, 0, payload.Length);
                    pipe.Flush();

                    using (var ms = new MemoryStream())
                    {
                        var buffer = new byte[4096];
                        do
                        {
                            int read = pipe.Read(buffer, 0, buffer.Length);
                            if (read == 0) break;
                            ms.Write(buffer, 0, read);
                        } while (!pipe.IsMessageComplete);

                        var data = ms.ToArray();
                        if (data.Length == 0) return ControlResponse.Fail("Empty response.");
                        return ControlProtocol.Deserialize<ControlResponse>(data, data.Length);
                    }
                }
            }
            catch (TimeoutException)
            {
                return ControlResponse.Fail("Recorder not running.");
            }
            catch (Exception ex)
            {
                return ControlResponse.Fail(ex.Message);
            }
        }
    }
}

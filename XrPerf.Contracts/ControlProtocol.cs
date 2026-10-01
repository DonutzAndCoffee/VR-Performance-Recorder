using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace XrPerf.Contracts
{
    public static class ControlProtocol
    {
        public const string PipeName = "XrPerf.Control";

        /// <summary>HKCU key where the recorder app publishes its executable path.</summary>
        public const string AppRegistryKey = @"Software\XrPerf";
        public const string AppPathValue = "AppPath";

        public static class Commands
        {
            public const string Status = "Status";
            public const string Start = "Start";
            public const string Stop = "Stop";
            public const string Lap = "Lap";
            public const string ListSessions = "ListSessions";
            public const string OpenSession = "OpenSession";
            /// <summary>Opens the comparison window with 2-3 sessions (<see cref="ControlRequest.SessionPaths"/>).</summary>
            public const string CompareSessions = "CompareSessions";
        }

        public const int MaxCompareSessions = 3;

        private static readonly DataContractJsonSerializerSettings SerializerSettings = new DataContractJsonSerializerSettings
        {
            UseSimpleDictionaryFormat = true,
        };

        public static byte[] Serialize<T>(T value)
        {
            var serializer = new DataContractJsonSerializer(typeof(T), SerializerSettings);
            using (var stream = new MemoryStream())
            {
                serializer.WriteObject(stream, value);
                return stream.ToArray();
            }
        }

        public static T Deserialize<T>(byte[] data, int count)
        {
            var serializer = new DataContractJsonSerializer(typeof(T), SerializerSettings);
            using (var stream = new MemoryStream(data, 0, count))
            {
                return (T)serializer.ReadObject(stream);
            }
        }

        public static string ToJson<T>(T value) => Encoding.UTF8.GetString(Serialize(value));
    }

    [DataContract]
    public sealed class ControlRequest
    {
        [DataMember] public string Command { get; set; }
        [DataMember(EmitDefaultValue = false)] public string Label { get; set; }
        [DataMember(EmitDefaultValue = false)] public Dictionary<string, string> Context { get; set; }
        [DataMember(EmitDefaultValue = false)] public int LapNumber { get; set; }
        [DataMember(EmitDefaultValue = false)] public int MaxCount { get; set; }
        [DataMember(EmitDefaultValue = false)] public string SessionPath { get; set; }
        /// <summary>Session CSV paths for <see cref="ControlProtocol.Commands.CompareSessions"/> (A, B, C).</summary>
        [DataMember(EmitDefaultValue = false)] public List<string> SessionPaths { get; set; }
        /// <summary>Aggregation interval per CSV row in ms (0 = default 1000).</summary>
        [DataMember(EmitDefaultValue = false)] public int RowIntervalMs { get; set; }
        /// <summary>Additionally write every single frame to "_frames.csv".</summary>
        [DataMember(EmitDefaultValue = false)] public bool RawFrames { get; set; }
    }

    [DataContract]
    public sealed class ControlResponse
    {
        [DataMember] public bool Success { get; set; }
        [DataMember(EmitDefaultValue = false)] public string Error { get; set; }
        [DataMember(EmitDefaultValue = false)] public RecorderStatus Status { get; set; }
        [DataMember(EmitDefaultValue = false)] public List<SessionSummary> Sessions { get; set; }

        public static ControlResponse Ok(RecorderStatus status = null) => new ControlResponse { Success = true, Status = status };
        public static ControlResponse Fail(string error) => new ControlResponse { Success = false, Error = error };
    }

    [DataContract]
    public sealed class RecorderStatus
    {
        [DataMember] public bool IsRecording { get; set; }
        [DataMember] public bool LayerConnected { get; set; }
        [DataMember] public string AppName { get; set; }
        [DataMember] public string RuntimeName { get; set; }
        [DataMember] public double CurrentFps { get; set; }
        [DataMember] public int CurrentLap { get; set; }
        [DataMember] public double RecordingSeconds { get; set; }
        [DataMember] public string CurrentSessionPath { get; set; }
        [DataMember] public string LastSessionPath { get; set; }
    }

    [DataContract]
    public sealed class SessionSummary
    {
        [DataMember] public string Path { get; set; }
        [DataMember] public string AppName { get; set; }
        [DataMember] public string Label { get; set; }
        [DataMember] public DateTime StartTime { get; set; }
        [DataMember] public double DurationSeconds { get; set; }
        [DataMember] public double AverageFps { get; set; }
        [DataMember] public Dictionary<string, string> Context { get; set; }
    }
}

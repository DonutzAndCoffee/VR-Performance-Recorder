using GameReaderCommon;
using SimHub.Plugins;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using XrPerf.Contracts;

namespace XrPerf.SimHubPlugin
{
    [PluginDescription("XrPerf - remote control for the unified OpenXR performance recorder")]
    [PluginAuthor("Don Utz")]
    [PluginName("XrPerf Recorder")]
    public class XrPerfPlugin : IPlugin, IDataPlugin, IWPFSettingsV2
    {
        private const string SettingsKey = "XrPerfPlugin";

        public PluginManager PluginManager { get; set; }
        public ImageSource PictureIcon => null;
        public string LeftMenuTitle => "XrPerf Recorder";

        public XrPerfPluginSettings Settings { get; private set; }
        public RecorderStatus Status { get; private set; } = new RecorderStatus();
        public bool RecorderReachable { get; private set; }
        public string LastError { get; private set; } = string.Empty;

        private readonly XrPerfClient _client = new XrPerfClient();
        private readonly object _sync = new object();
        private Timer _pollTimer;

        // Latest game context snapshot, updated from DataUpdate.
        private string _game = string.Empty;
        private string _track = string.Empty;
        private string _car = string.Empty;
        private string _sessionType = string.Empty;
        private bool _gameRunning;
        private int _lastLap = -1;

        public event Action StatusChanged;

        public void Init(PluginManager pluginManager)
        {
            Settings = this.ReadCommonSettings(SettingsKey, () => new XrPerfPluginSettings());

            this.AttachDelegate("XrPerf.RecorderReachable", () => RecorderReachable);
            this.AttachDelegate("XrPerf.IsRecording", () => Status.IsRecording);
            this.AttachDelegate("XrPerf.LayerConnected", () => Status.LayerConnected);
            this.AttachDelegate("XrPerf.AppName", () => Status.AppName);
            this.AttachDelegate("XrPerf.Runtime", () => Status.RuntimeName);
            this.AttachDelegate("XrPerf.Fps", () => Status.CurrentFps);
            this.AttachDelegate("XrPerf.Lap", () => Status.CurrentLap);
            this.AttachDelegate("XrPerf.RecordingSeconds", () => Status.RecordingSeconds);
            this.AttachDelegate("XrPerf.LastError", () => LastError);

            this.AddAction("XrPerfStart", (a, b) => StartRecording());
            this.AddAction("XrPerfStop", (a, b) => StopRecording());
            this.AddAction("XrPerfToggle", (a, b) =>
            {
                if (Status.IsRecording) StopRecording(); else StartRecording();
            });
            this.AddAction("XrPerfOpenLastSession", (a, b) => OpenLastSession());
            this.AddAction("XrPerfMarker", (a, b) => AddMarker());
            this.AddAction("XrPerfCompareLastSessions", (a, b) => CompareLastSessions());

            _pollTimer = new Timer(_ => PollStatus(), null, 0, 1000);
            SimHub.Logging.Current.Info("[XrPerf] Plugin initialized");
        }

        public void End(PluginManager pluginManager)
        {
            _pollTimer?.Dispose();
            this.SaveCommonSettings(SettingsKey, Settings);
        }

        public void DataUpdate(PluginManager pluginManager, ref GameData data)
        {
            bool running = data.GameRunning && data.NewData != null;

            if (running)
            {
                _game = data.GameName ?? string.Empty;
                _track = data.NewData.TrackName ?? string.Empty;
                _car = data.NewData.CarModel ?? string.Empty;
                string sessionType = data.NewData.SessionTypeName ?? string.Empty;

                if (!_gameRunning && Settings.AutoStartOnSession && !Status.IsRecording)
                    StartRecording();
                else if (_gameRunning && sessionType != _sessionType && Settings.AutoStartOnSession && Status.IsRecording)
                {
                    // Session type changed (e.g. practice -> race): split into a new recording.
                    _sessionType = sessionType;
                    Fire(() => { Send(ControlProtocol.Commands.Stop); Send(BuildStartRequest()); });
                }
                _sessionType = sessionType;

                if (Settings.TrackLaps && Status.IsRecording)
                {
                    int lap = data.NewData.CurrentLap;
                    if (lap > 0 && lap != _lastLap)
                    {
                        _lastLap = lap;
                        Fire(() => _client.Send(new ControlRequest { Command = ControlProtocol.Commands.Lap, LapNumber = lap }));
                    }
                }
            }
            else if (_gameRunning && Settings.AutoStopOnSessionEnd && Status.IsRecording)
            {
                StopRecording();
            }

            _gameRunning = running;
        }

        public System.Windows.Controls.Control GetWPFSettingsControl(PluginManager pluginManager)
        {
            return new SettingsControl(this);
        }

        public void StartRecording()
        {
            _lastLap = -1;
            Fire(() => Send(BuildStartRequest()));
        }

        public void StopRecording() => Fire(() => Send(ControlProtocol.Commands.Stop));

        /// <summary>Manual marker: advances the lap counter so a section can be found in the analysis.</summary>
        public void AddMarker()
        {
            Fire(() =>
            {
                if (!Status.IsRecording) return;
                int next = Status.CurrentLap + 1;
                _lastLap = next;
                Send(new ControlRequest { Command = ControlProtocol.Commands.Lap, LapNumber = next });
            });
        }

        public void OpenLastSession()
        {
            Fire(() =>
            {
                var path = Status.LastSessionPath;
                if (string.IsNullOrEmpty(path)) return;
                Send(new ControlRequest { Command = ControlProtocol.Commands.OpenSession, SessionPath = path });
            });
        }

        public List<SessionSummary> ListSessions(int maxCount = 50)
        {
            var response = _client.Send(new ControlRequest { Command = ControlProtocol.Commands.ListSessions, MaxCount = maxCount });
            return response.Success && response.Sessions != null ? response.Sessions : new List<SessionSummary>();
        }

        public void OpenSession(string path)
        {
            Fire(() => Send(new ControlRequest { Command = ControlProtocol.Commands.OpenSession, SessionPath = path }));
        }

        /// <summary>Opens the comparison window in openXRTK Graph with 2-3 sessions (A, B, C in the given order).</summary>
        public void CompareSessions(IList<string> paths)
        {
            if (paths == null || paths.Count < 2) return;
            var list = new List<string>(paths);
            if (list.Count > ControlProtocol.MaxCompareSessions)
                list.RemoveRange(ControlProtocol.MaxCompareSessions, list.Count - ControlProtocol.MaxCompareSessions);
            Fire(() => Send(new ControlRequest { Command = ControlProtocol.Commands.CompareSessions, SessionPaths = list }));
        }

        /// <summary>Deletes the given sessions including all their files. Returns an error message or null on success.</summary>
        public string DeleteSessions(IList<string> paths)
        {
            if (paths == null || paths.Count == 0) return null;
            var response = _client.Send(new ControlRequest { Command = ControlProtocol.Commands.DeleteSessions, SessionPaths = new List<string>(paths) });
            return response.Success ? null : (response.Error ?? "Deleting failed.");
        }

        /// <summary>Compares the two most recent sessions (older = A, newer = B).</summary>
        public void CompareLastSessions()
        {
            Fire(() =>
            {
                var sessions = ListSessions(2);
                if (sessions.Count < 2) return;
                Send(new ControlRequest
                {
                    Command = ControlProtocol.Commands.CompareSessions,
                    SessionPaths = new List<string> { sessions[1].Path, sessions[0].Path },
                });
            });
        }

        public void SaveSettings() => this.SaveCommonSettings(SettingsKey, Settings);

        public static string FindAppPath()
        {
            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(ControlProtocol.AppRegistryKey))
                {
                    var path = key?.GetValue(ControlProtocol.AppPathValue) as string;
                    return !string.IsNullOrEmpty(path) && System.IO.File.Exists(path) ? path : null;
                }
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Starts openXRTK Graph. Returns false if its location is unknown.</summary>
        public bool LaunchApp()
        {
            var path = FindAppPath();
            if (path == null) return false;
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path)
                {
                    UseShellExecute = true,
                    WorkingDirectory = System.IO.Path.GetDirectoryName(path),
                });
                return true;
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Error("[XrPerf] Launch failed: " + ex.Message);
                return false;
            }
        }

        internal Dictionary<string, string> BuildContext()
        {
            var ctx = new Dictionary<string, string>();
            void Put(string key, string value)
            {
                if (!string.IsNullOrWhiteSpace(value)) ctx[key] = value.Trim();
            }

            Put("Game", _game);
            Put("Track", _track);
            Put("Car", _car);
            Put("SessionType", _sessionType);
            Put("Resolution", Settings.Resolution);
            Put("AntiAliasing", Settings.AntiAliasing);
            Put("Notes", Settings.Notes);
            Put("Source", "SimHub");
            return ctx;
        }

        private ControlRequest BuildStartRequest()
        {
            var label = (Settings.LabelTemplate ?? string.Empty)
                .Replace("{Game}", _game)
                .Replace("{Track}", _track)
                .Replace("{Car}", _car)
                .Replace("{SessionType}", _sessionType);
            label = string.Join(" ", label.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries));

            return new ControlRequest
            {
                Command = ControlProtocol.Commands.Start,
                Label = string.IsNullOrEmpty(label) ? null : label,
                Context = BuildContext(),
                RowIntervalMs = Settings.RowIntervalMs,
                RawFrames = Settings.RawFrames,
            };
        }

        private void Send(string command) => Send(new ControlRequest { Command = command });

        private void Send(ControlRequest request)
        {
            var response = _client.Send(request);
            Apply(response);
            if (!response.Success)
                SimHub.Logging.Current.Warn($"[XrPerf] {request.Command} failed: {response.Error}");
        }

        private void PollStatus()
        {
            Apply(_client.Send(new ControlRequest { Command = ControlProtocol.Commands.Status }));
        }

        private void Apply(ControlResponse response)
        {
            lock (_sync)
            {
                RecorderReachable = response.Success || response.Status != null;
                if (response.Status != null) Status = response.Status;
                else if (!response.Success) Status = new RecorderStatus();
                LastError = response.Success ? string.Empty : (response.Error ?? string.Empty);
            }
            StatusChanged?.Invoke();
        }

        // Pipe calls must never block SimHub's data loop.
        private static void Fire(Action action)
        {
            Task.Run(() =>
            {
                try { action(); }
                catch (Exception ex) { SimHub.Logging.Current.Error("[XrPerf] " + ex.Message); }
            });
        }
    }
}

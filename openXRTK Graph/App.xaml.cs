using System.Configuration;
using System.Data;
using System.Windows;
using openXRTK_Graph.XrPerf;

namespace openXRTK_Graph
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        public static XrPerfRecorder? Recorder { get; private set; }
        private XrPerfControlServer? _controlServer;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            Recorder = new XrPerfRecorder();
            OverlaySettings.Set(OverlaySettings.RecordingActive, false);
            Recorder.RecordingStateChanged += recording =>
            {
                OverlaySettings.Set(OverlaySettings.RecordingActive, recording);
                UpdateProcessLogger(recording);
                RecordingIndicator.Play(recording);
                Dispatcher.BeginInvoke(() => (MainWindow as openXRTK_Graph.MainWindow)?.SetRecordingIndicator(recording));
            };
            _controlServer = new XrPerfControlServer(Recorder);
            _controlServer.OpenSessionRequested += path => Dispatcher.BeginInvoke(() => OpenSession(path));
            _controlServer.CompareSessionsRequested += paths => Dispatcher.BeginInvoke(() => CompareSessions(paths));

            RegisterAppLocation();
        }

        private LiveProcessLogger? _processLogger;
        private readonly object _processLoggerLock = new();

        /// <summary>Records a process log next to every VR session CSV when enabled in settings.</summary>
        private void UpdateProcessLogger(bool recording)
        {
            lock (_processLoggerLock)
            {
                _processLogger?.Stop();
                _processLogger?.Dispose();
                _processLogger = null;

                if (!recording || !OverlaySettings.Get(OverlaySettings.ProcessLogging, false)) return;
                string? session = Recorder?.GetStatus().CurrentSessionPath;
                if (string.IsNullOrEmpty(session)) return;
                try
                {
                    string path = System.IO.Path.ChangeExtension(session, null) + "_process.csv";
                    int interval = OverlaySettings.Get(OverlaySettings.ProcessLogIntervalMs, 1000);
                    _processLogger = new LiveProcessLogger(interval, filePath: path);
                    _processLogger.Start();
                }
                catch (Exception) { _processLogger = null; }
            }
        }

        /// <summary>Lets the SimHub plugin
        private static void RegisterAppLocation()
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(global::XrPerf.Contracts.ControlProtocol.AppRegistryKey);
                key.SetValue(global::XrPerf.Contracts.ControlProtocol.AppPathValue, Environment.ProcessPath ?? string.Empty);
            }
            catch (Exception) { }
        }

        private void OpenSession(string path)
        {
            if (MainWindow is not openXRTK_Graph.MainWindow window) return;

            if (window.WindowState == WindowState.Minimized)
                window.WindowState = WindowState.Normal;
            window.Activate();
            window.LoadFile(path);
        }

        private void CompareSessions(IReadOnlyList<string> paths)
        {
            var window = new CompareWindow { Owner = MainWindow };
            window.Show();
            window.Activate();
            window.LoadFiles(paths);
        }

        protected override void OnExit(ExitEventArgs e)
        {
            _controlServer?.Dispose();
            Recorder?.Dispose();
            UpdateProcessLogger(false);
            OverlaySettings.Set(OverlaySettings.RecordingActive, false);
            base.OnExit(e);
        }
    }

}

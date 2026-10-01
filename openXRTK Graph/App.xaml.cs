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
            _controlServer = new XrPerfControlServer(Recorder);
            _controlServer.OpenSessionRequested += path => Dispatcher.BeginInvoke(() => OpenSession(path));

            RegisterAppLocation();
        }

        /// <summary>Lets the SimHub plugin find and launch this app.</summary>
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

        protected override void OnExit(ExitEventArgs e)
        {
            _controlServer?.Dispose();
            Recorder?.Dispose();
            base.OnExit(e);
        }
    }

}

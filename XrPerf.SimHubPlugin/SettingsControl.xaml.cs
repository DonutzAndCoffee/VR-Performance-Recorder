using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using XrPerf.Contracts;

namespace XrPerf.SimHubPlugin
{
    public partial class SettingsControl : UserControl
    {
        private readonly XrPerfPlugin _plugin;

        public SettingsControl(XrPerfPlugin plugin)
        {
            InitializeComponent();
            _plugin = plugin;

            var s = plugin.Settings;
            ChkAutoStart.IsChecked = s.AutoStartOnSession;
            ChkAutoStop.IsChecked = s.AutoStopOnSessionEnd;
            ChkLaps.IsChecked = s.TrackLaps;
            ChkRawFrames.IsChecked = s.RawFrames;
            CmbInterval.SelectedIndex = 0;
            for (int i = 0; i < CmbInterval.Items.Count; i++)
                if (CmbInterval.Items[i] is ComboBoxItem item && (string)item.Tag == s.RowIntervalMs.ToString())
                    CmbInterval.SelectedIndex = i;
            TxtLabel.Text = s.LabelTemplate;
            TxtResolution.Text = s.Resolution;
            TxtAa.Text = s.AntiAliasing;
            TxtNotes.Text = s.Notes;

            Loaded += (_, __) => { _plugin.StatusChanged += OnStatusChanged; UpdateStatus(); RefreshSessions(); };
            Unloaded += (_, __) => _plugin.StatusChanged -= OnStatusChanged;
        }

        private bool? _lastRecording;
        private bool _lastReachable;
        private string _lastSessionKey;
        private bool _refreshing;
        private bool _refreshPending;

        private async void RefreshSessions()
        {
            if (_refreshing) { _refreshPending = true; return; }
            _refreshing = true;
            try
            {
                var sessions = await Task.Run(() => _plugin.ListSessions());
                if (sessions.Count > 0 || _plugin.RecorderReachable)
                    LstSessions.ItemsSource = sessions;
            }
            catch (System.Exception) { }
            finally { _refreshing = false; }
            if (_refreshPending) { _refreshPending = false; RefreshSessions(); }
        }

        private void OnStatusChanged() => Dispatcher.BeginInvoke(new System.Action(UpdateStatus));

        private void UpdateStatus()
        {
            var st = _plugin.Status;
            bool reachable = _plugin.RecorderReachable;
            bool recording = reachable && st.IsRecording;
            string sessionKey = reachable ? $"{recording}|{st.CurrentSessionPath}|{st.LastSessionPath}" : null;
            if (_lastRecording.HasValue && (sessionKey != _lastSessionKey || _lastReachable != reachable))
                RefreshSessions();
            _lastSessionKey = sessionKey;
            _lastRecording = recording;
            _lastReachable = reachable;
            PnlNotRunning.Visibility = reachable ? Visibility.Collapsed : Visibility.Visible;
            if (!reachable)
            {
                bool known = XrPerfPlugin.FindAppPath() != null;
                BtnLaunch.IsEnabled = known;
                TxtLaunchHint.Text = known ? string.Empty : "Start openXRTK Graph once manually so its location is known.";
            }

            if (!reachable)
                TxtStatus.Text = "openXRTK Graph is not running.";
            else if (st.IsRecording)
                TxtStatus.Text = $"Recording {st.RecordingSeconds:F0}s - {st.AppName} - {st.CurrentFps:F1} FPS - lap {st.CurrentLap}";
            else
                TxtStatus.Text = st.LayerConnected ? $"Idle - layer connected ({st.AppName}, {st.RuntimeName})" : "Idle - no OpenXR/OpenVR app detected";
        }

        private void BtnStart_Click(object sender, RoutedEventArgs e) => _plugin.StartRecording();

        private void BtnLaunch_Click(object sender, RoutedEventArgs e)
        {
            if (_plugin.LaunchApp())
            {
                BtnLaunch.IsEnabled = false;
                TxtLaunchHint.Text = "Starting…";
            }
            else
            {
                TxtLaunchHint.Text = "openXRTK Graph could not be started.";
            }
        }
        private void BtnStop_Click(object sender, RoutedEventArgs e) => _plugin.StopRecording();
        private void BtnOpenLast_Click(object sender, RoutedEventArgs e) => _plugin.OpenLastSession();

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            var s = _plugin.Settings;
            s.AutoStartOnSession = ChkAutoStart.IsChecked == true;
            s.AutoStopOnSessionEnd = ChkAutoStop.IsChecked == true;
            s.TrackLaps = ChkLaps.IsChecked == true;
            s.RawFrames = ChkRawFrames.IsChecked == true;
            if (CmbInterval.SelectedItem is ComboBoxItem sel && int.TryParse((string)sel.Tag, out var ms))
                s.RowIntervalMs = ms;
            s.LabelTemplate = TxtLabel.Text;
            s.Resolution = TxtResolution.Text;
            s.AntiAliasing = TxtAa.Text;
            s.Notes = TxtNotes.Text;
            _plugin.SaveSettings();
        }

        private void BtnRefresh_Click(object sender, RoutedEventArgs e) => RefreshSessions();

        private void LstSessions_DoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (LstSessions.SelectedItem is SessionSummary session)
                _plugin.OpenSession(session.Path);
        }

        private void LstSessions_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            int n = LstSessions.SelectedItems.Count;
            BtnCompare.IsEnabled = n >= 2 && n <= ControlProtocol.MaxCompareSessions;
            BtnDelete.IsEnabled = n >= 1;
        }

        private async void BtnDelete_Click(object sender, RoutedEventArgs e)
        {
            var selected = LstSessions.SelectedItems.OfType<SessionSummary>().ToList();
            if (selected.Count == 0) return;
            int files = selected.Sum(s => s.FileCount);
            if (MessageBox.Show($"Delete {selected.Count} session(s) with {files} file(s)?", "Delete sessions",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;

            var paths = selected.Select(s => s.Path).ToList();
            var error = await Task.Run(() => _plugin.DeleteSessions(paths));
            if (error != null)
                MessageBox.Show(error, "Delete sessions", MessageBoxButton.OK, MessageBoxImage.Error);
            LstSessions.ItemsSource = await Task.Run(() => _plugin.ListSessions());
        }

        private void BtnCompare_Click(object sender, RoutedEventArgs e)
        {
            var paths = LstSessions.SelectedItems.OfType<SessionSummary>()
                .OrderBy(s => s.StartTime)
                .Select(s => s.Path)
                .ToList();
            _plugin.CompareSessions(paths);
        }
    }
}

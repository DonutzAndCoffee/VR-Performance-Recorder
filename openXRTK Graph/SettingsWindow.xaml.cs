using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace openXRTK_Graph;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();

        ChkRecSound.IsChecked = RecordingIndicator.SoundEnabled;
        ChkRecSound.Checked += (_, _) => RecordingIndicator.SoundEnabled = true;
        ChkRecSound.Unchecked += (_, _) => RecordingIndicator.SoundEnabled = false;

        ChkProcessLog.IsChecked = OverlaySettings.Get(OverlaySettings.ProcessLogging, false);
        ChkProcessLog.Checked += (_, _) => OverlaySettings.Set(OverlaySettings.ProcessLogging, true);
        ChkProcessLog.Unchecked += (_, _) => OverlaySettings.Set(OverlaySettings.ProcessLogging, false);
        int procInterval = OverlaySettings.Get(OverlaySettings.ProcessLogIntervalMs, 1000);
        CmbProcessInterval.SelectedItem = CmbProcessInterval.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(i => i.Tag as string == procInterval.ToString()) ?? CmbProcessInterval.Items[3];
        CmbProcessInterval.SelectionChanged += (_, _) =>
        {
            if (CmbProcessInterval.SelectedItem is ComboBoxItem { Tag: string tag } && int.TryParse(tag, out var value))
                OverlaySettings.Set(OverlaySettings.ProcessLogIntervalMs, value);
        };

        (CheckBox Box, string Name, bool Default)[] toggles =
        [
            (ChkEnabled, "Enabled", false),
            (ChkAutoWithRecording, "AutoWithRecording", false),
            (ChkFps, "ShowFps", true),
            (ChkFrameTime, "ShowFrameTime", true),
            (ChkCpu, "ShowCpu", true),
            (ChkGpu, "ShowGpu", true),
            (ChkResolution, "ShowResolution", false),
            (ChkApp, "ShowApp", false),
        ];
        foreach (var (box, name, def) in toggles)
        {
            box.IsChecked = OverlaySettings.Get(name, def);
            box.Checked += (_, _) => OverlaySettings.Set(name, true);
            box.Unchecked += (_, _) => OverlaySettings.Set(name, false);
        }

        CmbPosition.SelectedIndex = Math.Clamp(OverlaySettings.Get("Position", 0), 0, 3);
        CmbPosition.SelectionChanged += (_, _) => OverlaySettings.Set("Position", CmbPosition.SelectedIndex);

        var scale = OverlaySettings.Get("Scale", 100);
        CmbScale.SelectedIndex = scale < 90 ? 0 : scale > 120 ? 2 : 1;
        CmbScale.SelectionChanged += (_, _) =>
        {
            if (CmbScale.SelectedItem is ComboBoxItem { Tag: string tag } && int.TryParse(tag, out var value))
                OverlaySettings.Set("Scale", value);
        };

        UpdateLayerUi();
        UpdateSimHubUi();

        var reader = new global::XrPerf.Contracts.LiveStatsReader();
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        timer.Tick += (_, _) => TxtStatus.Text = DescribeOverlayStatus(reader.Read());
        timer.Start();
        Closed += (_, _) => { timer.Stop(); reader.Dispose(); };
    }

    // ---- Shared status helpers (also used by the main window) ----

    public static (string Text, Brush Brush) GetLayerStatus()
    {
        var (state, _) = XrPerf.LayerManager.GetState();
        return state switch
        {
            XrPerf.LayerManager.LayerState.Enabled => ("Active", Brushes.LimeGreen),
            XrPerf.LayerManager.LayerState.Disabled => ("Disabled", Brushes.Orange),
            XrPerf.LayerManager.LayerState.Missing => ("Files missing", Brushes.OrangeRed),
            _ => ("Not installed", Brushes.Gray),
        };
    }

    public static (string Text, Brush Brush) GetSimHubStatus()
    {
        var dir = XrPerf.SimHubPluginInstaller.FindSimHubDirectory();
        if (dir == null) return ("SimHub not found", Brushes.Gray);
        return XrPerf.SimHubPluginInstaller.IsUpToDate(dir) switch
        {
            true => ("Installed", Brushes.LimeGreen),
            false => ("Update available", Brushes.Orange),
            null => ("Not installed", Brushes.Gray),
        };
    }

    private static string DescribeOverlayStatus(global::XrPerf.Contracts.LiveStats s)
    {
        if (!s.Connected) return "Status: no OpenXR app running with the XrPerf layer.";
        string overlay = s.OverlayStatus switch
        {
            global::XrPerf.Contracts.OverlayStatus.Active => "overlay active",
            global::XrPerf.Contracts.OverlayStatus.Disabled => "overlay hidden",
            global::XrPerf.Contracts.OverlayStatus.UnsupportedGraphicsApi => $"overlay not supported for {s.GraphicsApi} (D3D11 only)",
            global::XrPerf.Contracts.OverlayStatus.NothingSelected => "nothing selected to display",
            _ => $"overlay error: {s.OverlayStatus}",
        };
        return $"Status: {s.AppName} ({s.GraphicsApi}) - {overlay}";
    }

    // ---- Layer ----

    private void UpdateLayerUi()
    {
        var (state, _) = XrPerf.LayerManager.GetState();
        (TxtLayerState.Text, TxtLayerState.Foreground) = GetLayerStatus();

        bool registered = state != XrPerf.LayerManager.LayerState.NotInstalled;
        BtnLayerInstall.Content = registered ? "Reinstall" : "Install";
        BtnLayerInstall.IsEnabled = XrPerf.LayerManager.BundledLayerAvailable;
        BtnLayerToggle.IsEnabled = state is XrPerf.LayerManager.LayerState.Enabled or XrPerf.LayerManager.LayerState.Disabled;
        BtnLayerToggle.Content = state == XrPerf.LayerManager.LayerState.Disabled ? "Enable" : "Disable";
        BtnLayerUninstall.IsEnabled = registered;

        TxtLayerState.ToolTip = string.Join(Environment.NewLine,
            XrPerf.LayerManager.ListLayers().Select(l => $"[{(l.Enabled ? "on " : "off")}] {l.Path}")
                .DefaultIfEmpty("No implicit OpenXR layers registered."));
    }

    private void RunLayerAction(Func<bool> action)
    {
        if (!action())
            MessageBox.Show("The change was not applied (administrator approval cancelled or failed).",
                "Perf Layer", MessageBoxButton.OK, MessageBoxImage.Warning);
        UpdateLayerUi();
    }

    private void BtnLayerInstall_Click(object sender, RoutedEventArgs e) => RunLayerAction(XrPerf.LayerManager.Install);

    private void BtnLayerUninstall_Click(object sender, RoutedEventArgs e) => RunLayerAction(XrPerf.LayerManager.Uninstall);

    private void BtnLayerToggle_Click(object sender, RoutedEventArgs e)
    {
        var (state, path) = XrPerf.LayerManager.GetState();
        if (path == null) return;
        RunLayerAction(() => XrPerf.LayerManager.SetEnabled(path, state == XrPerf.LayerManager.LayerState.Disabled));
    }

    // ---- SimHub ----

    private void UpdateSimHubUi()
    {
        (TxtSimHubState.Text, TxtSimHubState.Foreground) = GetSimHubStatus();
        var dir = XrPerf.SimHubPluginInstaller.FindSimHubDirectory();
        BtnSimHubPlugin.IsEnabled = dir != null && XrPerf.SimHubPluginInstaller.BundleAvailable;
        BtnSimHubPlugin.Content = (dir == null ? null : XrPerf.SimHubPluginInstaller.IsUpToDate(dir)) switch
        {
            true => "Reinstall SimHub Plugin",
            false => "Update SimHub Plugin",
            null => "Install SimHub Plugin",
        };
        BtnSimHubPlugin.ToolTip = dir == null ? "SimHub installation not found." :
            !XrPerf.SimHubPluginInstaller.BundleAvailable ? "Plugin files not bundled with this build." :
            $"Copies the XrPerf plugin to {dir}";
    }

    private void BtnSimHubPlugin_Click(object sender, RoutedEventArgs e)
    {
        var dir = XrPerf.SimHubPluginInstaller.FindSimHubDirectory();
        if (dir == null) return;

        while (XrPerf.SimHubPluginInstaller.IsSimHubRunning())
        {
            if (MessageBox.Show("Please close SimHub first, then click OK.", "SimHub Plugin",
                    MessageBoxButton.OKCancel, MessageBoxImage.Information) != MessageBoxResult.OK)
                return;
        }

        if (XrPerf.SimHubPluginInstaller.Install(dir))
            MessageBox.Show("Plugin installed. Start SimHub and enable \"XrPerf Recorder\" when prompted.",
                "SimHub Plugin", MessageBoxButton.OK, MessageBoxImage.Information);
        else
            MessageBox.Show("The plugin was not installed (administrator approval cancelled or copy failed).",
                "SimHub Plugin", MessageBoxButton.OK, MessageBoxImage.Warning);

        UpdateSimHubUi();
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();
}

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
        BuildButtonBindingsUi();

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

    // ---- Controller buttons ----

    private void BuildButtonBindingsUi()
    {
        var manager = App.Buttons;
        if (manager is null)
        {
            PnlButtonBindings.Children.Add(new TextBlock { Text = "DirectInput is not available.", Foreground = Brushes.OrangeRed });
            return;
        }

        (Input.ButtonAction Action, string Label)[] actions =
        [
            (Input.ButtonAction.Toggle, "Start/Stop"),
            (Input.ButtonAction.Start, "Start"),
            (Input.ButtonAction.Stop, "Stop"),
            (Input.ButtonAction.Marker, "Marker"),
        ];

        var labels = new Dictionary<Input.ButtonAction, TextBlock>();
        var assignButtons = new List<Button>();

        void Refresh(Input.ButtonAction action) =>
            labels[action].Text = manager.GetBinding(action)?.ToString() ?? "Not assigned";

        foreach (var (action, label) in actions)
        {
            var grid = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            grid.Children.Add(new TextBlock { Text = label + ":", VerticalAlignment = VerticalAlignment.Center });
            var value = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = Brushes.White };
            Grid.SetColumn(value, 1);
            grid.Children.Add(value);
            labels[action] = value;

            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            Grid.SetColumn(buttons, 2);
            var assign = new Button { Content = "Assign" };
            var clear = new Button { Content = "Clear", Margin = new Thickness(0) };
            assign.Click += (_, _) =>
            {
                if (manager.IsLearning)
                {
                    manager.CancelLearning();
                    foreach (var a in actions) Refresh(a.Action);
                    foreach (var b in assignButtons) b.Content = "Assign";
                    return;
                }
                value.Text = "Press a button...";
                assign.Content = "Cancel";
                manager.StartLearning(action);
            };
            clear.Click += (_, _) =>
            {
                manager.SetBinding(action, null);
                Refresh(action);
            };
            buttons.Children.Add(assign);
            buttons.Children.Add(clear);
            grid.Children.Add(buttons);
            assignButtons.Add(assign);

            PnlButtonBindings.Children.Add(grid);
            Refresh(action);
        }

        Action<Input.ButtonAction, Input.ButtonBinding> onLearned = (action, _) => Dispatcher.BeginInvoke(() =>
        {
            Refresh(action);
            foreach (var b in assignButtons) b.Content = "Assign";
        });
        manager.Learned += onLearned;
        Closed += (_, _) =>
        {
            manager.Learned -= onLearned;
            manager.CancelLearning();
        };
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

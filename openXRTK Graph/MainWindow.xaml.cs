using System.Windows;
using Microsoft.Win32;

namespace openXRTK_Graph;

public partial class MainWindow : Window
{
    private List<CsvDataPoint> _data = [];
    private SessionMetadata? _meta;
    private LiveProcessLogger? _liveLogger;
    private List<FpsCorrelationAnalyzer.ProcessSnapshot> _processLog = [];
    private int _intervalMs = 1000;

    public MainWindow()
    {
        InitializeComponent();
        ChkRecSound.IsChecked = RecordingIndicator.SoundEnabled;
        SetRecordingIndicator(App.Recorder?.IsRecording ?? false);
        UpdateLayerUi();
        UpdateSimHubUi();
    }

    public void SetRecordingIndicator(bool recording)
    {
        TxtRecIndicator.Foreground = recording
            ? System.Windows.Media.Brushes.Red
            : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x55, 0x55, 0x55));
    }

    private void ChkRecSound_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        RecordingIndicator.SoundEnabled = ChkRecSound.IsChecked == true;
    }

    private void UpdateSimHubUi()
    {
        var dir = XrPerf.SimHubPluginInstaller.FindSimHubDirectory();
        BtnSimHubPlugin.IsEnabled = dir != null && XrPerf.SimHubPluginInstaller.BundleAvailable;
        var upToDate = dir == null ? null : XrPerf.SimHubPluginInstaller.IsUpToDate(dir);
        BtnSimHubPlugin.Content = upToDate switch
        {
            true => "SimHub Plugin ✓",
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

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        UpdateLayerUi();
    }

    private void UpdateLayerUi()
    {
        var (state, _) = XrPerf.LayerManager.GetState();
        (TxtLayerState.Text, TxtLayerState.Foreground) = state switch
        {
            XrPerf.LayerManager.LayerState.Enabled => ("Active", System.Windows.Media.Brushes.LimeGreen),
            XrPerf.LayerManager.LayerState.Disabled => ("Disabled", System.Windows.Media.Brushes.Orange),
            XrPerf.LayerManager.LayerState.Missing => ("Files missing", System.Windows.Media.Brushes.OrangeRed),
            _ => ("Not installed", System.Windows.Media.Brushes.Gray),
        };

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

    private void BtnOpen_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Open OpenXR Toolkit Statistics CSV",
            Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*",
            CheckFileExists = true,
        };

        if (dlg.ShowDialog() != true) return;

        LoadFile(dlg.FileName);
    }

    public void LoadFile(string fileName)
    {
        try
        {
            _data = CsvParser.Parse(fileName);
            if (_data.Count == 0)
            {
                MessageBox.Show("The file contained no valid data rows.", "Empty File", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _meta = CompanionDataLoader.TryLoad(fileName);
            TxtFilePath.Text = System.IO.Path.GetFileName(fileName);
            UpdateStats();
            UpdateCharts();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to load file:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void BtnStartLog_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_liveLogger != null) return;
            StartLogger(_intervalMs);
            MessageBox.Show($"Live process logging started. Writing to:\n{_liveLogger.LogFilePath}", "Live Log", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to start live logger:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void BtnStopLog_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_liveLogger == null) return;
            var path = _liveLogger.LogFilePath;
            StopLogger();
            MessageBox.Show($"Live process logging stopped. File saved to:\n{path}", "Live Log", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to stop live logger:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void StartLogger(int intervalMs)
    {
        if (_liveLogger != null) return;
        _liveLogger = new LiveProcessLogger(intervalMs);
        _liveLogger.Start();
        BtnStartLog.IsEnabled = false;
        BtnStopLog.IsEnabled = true;
    }

    private void StopLogger()
    {
        if (_liveLogger == null) return;
        _liveLogger.Stop();
        _liveLogger.Dispose();
        _liveLogger = null;
        BtnStartLog.IsEnabled = true;
        BtnStopLog.IsEnabled = false;
    }

    private void CmbInterval_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (CmbInterval.SelectedItem is System.Windows.Controls.ComboBoxItem it && int.TryParse(it.Tag?.ToString(), out var v))
        {
            _intervalMs = v;
            if (_liveLogger != null) _liveLogger.IntervalMs = v;
        }
    }

    private void UpdateStats()
    {
        double fpsAvg = _data.Average(d => d.Fps);
        double fpsMin = _data.Min(d => d.Fps);
        double fpsMax = _data.Max(d => d.Fps);
        double gpuAvg = _data.Average(d => d.AppGpuMs);
        double cpuAvg = _data.Average(d => d.AppCpuMs);
        double vramAvg = _data.Average(d => d.VramMb);

        TxtSamples.Text = $"Samples: {_data.Count:N0}";
        TxtFpsAvg.Text  = $"FPS avg: {fpsAvg:F1}";
        TxtFpsMin.Text  = $"FPS min: {fpsMin:F1}";
        TxtFpsMax.Text  = $"FPS max: {fpsMax:F1}";
        TxtGpuAvg.Text  = $"GPU avg: {gpuAvg:F2} ms";
        TxtCpuAvg.Text  = $"CPU avg: {cpuAvg:F2} ms";
        TxtVramAvg.Text = $"VRAM avg: {vramAvg:F0} MB";

        StatsPanel.Visibility = Visibility.Visible;
    }

    private void UpdateCharts()
    {
        // Frametime Distribution tab – CPU lines
        ChartCpuDist.Model = ChartBuilder.BuildFrametimeDistribution(
            _data, "CPU Frametime Distribution – App CPU / Render CPU", 0.2, includeCpu: true, includeGpu: false);

        // Frametime Distribution tab – GPU lines
        ChartGpuDist.Model = ChartBuilder.BuildFrametimeDistribution(
            _data, "GPU Frametime Distribution – App GPU", 0.2, includeCpu: false, includeGpu: true);
        HookDistributionSync();

        ChartFps.Model     = ChartBuilder.BuildFpsTimeSeries(_data);
        ChartOverlay.Model = ChartBuilder.BuildFpsAndFrametimeOverlay(_data);
        ChartCpuGpu.Model  = ChartBuilder.BuildCpuGpuTimeSeries(_data);
        ChartVram.Model    = ChartBuilder.BuildVramTimeSeries(_data);

        TxtAnalysisDist.Text    = AnalysisEngine.AnalyzeFrametimeDistribution(_data, _meta);
        TxtAnalysisFps.Text     = AnalysisEngine.AnalyzeFps(_data, _meta);
        TxtAnalysisOverlay.Text = AnalysisEngine.AnalyzeFpsAndFrametime(_data, _meta);
        TxtAnalysisCpuGpu.Text  = AnalysisEngine.AnalyzeCpuGpu(_data, _meta);
        TxtAnalysisVram.Text    = AnalysisEngine.AnalyzeVram(_data, _meta);
    }

    private bool _syncingDist;

    private void HookDistributionSync()
    {
        if (ChartCpuDist.Model is not { } cpu || ChartGpuDist.Model is not { } gpu) return;

        foreach (var pos in new[] { OxyPlot.Axes.AxisPosition.Bottom, OxyPlot.Axes.AxisPosition.Left })
        {
            var a = cpu.Axes.FirstOrDefault(x => x.Position == pos);
            var b = gpu.Axes.FirstOrDefault(x => x.Position == pos);
            if (a is null || b is null) continue;
            a.AxisChanged += (_, _) => SyncAxis(a, b, gpu);
            b.AxisChanged += (_, _) => SyncAxis(b, a, cpu);
        }
    }

    private void SyncAxis(OxyPlot.Axes.Axis source, OxyPlot.Axes.Axis target, OxyPlot.PlotModel targetModel)
    {
        if (_syncingDist || ChkSyncDist.IsChecked != true) return;
        _syncingDist = true;
        try
        {
            target.Zoom(source.ActualMinimum, source.ActualMaximum);
            targetModel.InvalidatePlot(false);
        }
        finally
        {
            _syncingDist = false;
        }
    }

    private void ChkSyncDist_Changed(object sender, RoutedEventArgs e)
    {
        if (ChkSyncDist.IsChecked != true || ChartCpuDist?.Model is not { } cpu || ChartGpuDist?.Model is not { } gpu) return;

        foreach (var a in cpu.Axes)
        {
            var b = gpu.Axes.FirstOrDefault(x => x.Position == a.Position);
            if (b is not null)
                SyncAxis(a, b, gpu);
        }
    }

    private void BtnCompare_Click(object sender, RoutedEventArgs e)
    {
        new CompareWindow().Show();
    }

    private void BtnLoadProcessLog_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Open Live Process Log CSV",
            Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*",
            CheckFileExists = true,
        };

        if (dlg.ShowDialog() != true) return;

        try
        {
            _processLog = FpsCorrelationAnalyzer.LoadProcessLog(dlg.FileName);
            TxtProcessLogPath.Text = System.IO.Path.GetFileName(dlg.FileName);
            TxtAnalysisDrops.Text = $"✓ Loaded {_processLog.Count} process snapshots from {System.IO.Path.GetFileName(dlg.FileName)}";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to load process log:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void BtnAnalyze_Click(object sender, RoutedEventArgs e)
    {
        if (_data.Count == 0)
        {
            MessageBox.Show("No FPS data loaded. Please open a CSV file first.", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (_processLog.Count == 0)
        {
            MessageBox.Show("No process log loaded. Please load a process log first.", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!double.TryParse(TxtTargetFps.Text, out var targetFps))
        {
            MessageBox.Show("Invalid target FPS value.", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!int.TryParse(TxtTimeWindow.Text, out var timeWindow) || timeWindow < 1)
        {
            MessageBox.Show("Invalid time window value. Must be >= 1 second.", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!double.TryParse(TxtMinCpuPercent.Text, out var minCpuPercent))
        {
            MessageBox.Show("Invalid min CPU% value.", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            var drops = FpsCorrelationAnalyzer.AnalyzeFpsDrops(_data, _processLog, targetFps, 0.98, 
                timeWindowSeconds: timeWindow, minCpuPercent: minCpuPercent);
            var analysis = FpsCorrelationAnalyzer.FormatFpsDropAnalysis(drops, maxDropsToShow: 30);

            TxtAnalysisDrops.Text = analysis;

            // Offer to export
            MessageBox.Show($"Found {drops.Count} FPS drop events.\n\nAnalysis displayed in the tab.", 
                "Analysis Complete", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Analysis failed:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}

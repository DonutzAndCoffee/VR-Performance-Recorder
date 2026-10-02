using System.Windows;
using Microsoft.Win32;

namespace openXRTK_Graph;

public partial class MainWindow : Window
{
    private List<CsvDataPoint> _data = [];
    private SessionMetadata? _meta;
    private List<FpsCorrelationAnalyzer.ProcessSnapshot> _processLog = [];

    public MainWindow()
    {
        InitializeComponent();
        SetRecordingIndicator(App.Recorder?.IsRecording ?? false);
        UpdateStatusUi();
    }

    public void SetRecordingIndicator(bool recording)
    {
        TxtRecIndicator.Foreground = recording
            ? System.Windows.Media.Brushes.Red
            : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x55, 0x55, 0x55));
    }

    private void UpdateStatusUi()
    {
        (TxtLayerState.Text, TxtLayerState.Foreground) = SettingsWindow.GetLayerStatus();
        (TxtSimHubState.Text, TxtSimHubState.Foreground) = SettingsWindow.GetSimHubStatus();
        TxtLayerState.ToolTip = string.Join(Environment.NewLine,
            XrPerf.LayerManager.ListLayers().Select(l => $"[{(l.Enabled ? "on " : "off")}] {l.Path}")
                .DefaultIfEmpty("No implicit OpenXR layers registered."));
    }

    private void BtnSettings_Click(object sender, RoutedEventArgs e)
    {
        new SettingsWindow { Owner = this }.ShowDialog();
        UpdateStatusUi();
    }

    private void BtnAbout_Click(object sender, RoutedEventArgs e)
    {
        new AboutWindow { Owner = this }.ShowDialog();
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        UpdateStatusUi();
    }

    private void BtnOpen_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Open Performance CSV",
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
            LoadFramesAnalysis(fileName);

            string processPath = System.IO.Path.ChangeExtension(fileName, null) + "_process.csv";
            if (System.IO.File.Exists(processPath))
            {
                _processLog = FpsCorrelationAnalyzer.LoadProcessLog(processPath);
                TxtProcessLogPath.Text = System.IO.Path.GetFileName(processPath);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to load file:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private string? _framesPath;

    private void LoadFramesAnalysis(string sessionCsvPath)
    {
        _framesPath = XrPerf.FramesAnalyzer.FindFramesFile(sessionCsvPath);
        TxtFramesPath.Text = _framesPath is null ? "None (session recorded without per-frame data)" : System.IO.Path.GetFileName(_framesPath);
        if (_framesPath is null)
        {
            TxtFramesReport.Text = "No _frames.csv found for this session.";
            return;
        }
        SetDetectedRefreshRate(TryReadRefreshRate(sessionCsvPath));
        _hmdInfo = TryReadHmdInfo(sessionCsvPath);
        RunFramesAnalysis();
    }

    private string _hmdInfo = "";

    private static string TryReadHmdInfo(string sessionCsvPath)
    {
        try
        {
            string json = XrPerf.XrPerfRecorder.GetCompanionPath(sessionCsvPath);
            if (!System.IO.File.Exists(json)) return "";
            using var doc = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(json));
            if (!doc.RootElement.TryGetProperty("Layer", out var l) || l.ValueKind != System.Text.Json.JsonValueKind.Object) return "";
            string S(string n) => l.TryGetProperty(n, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString() ?? "" : "";
            double D(string n) => l.TryGetProperty(n, out var v) && v.TryGetDouble(out double d) ? d : 0;

            var parts = new List<string>();
            if (S("SystemName") is { Length: > 0 } hmd) parts.Add($"Headset: {hmd}");
            if (D("DisplayRefreshRate") is > 0 and var hz) parts.Add($"{hz:F0} Hz");
            if (D("SwapchainWidth") is > 0 and var w && D("SwapchainHeight") is > 0 and var h)
            {
                double mp = w * h * Math.Max(1, D("ViewCount")) / 1e6;
                parts.Add($"Render resolution {w:F0} x {h:F0} per eye ({mp:F1} MPixel total)");
            }
            if (S("RuntimeName") is { Length: > 0 } rt) parts.Add($"Runtime: {rt}");
            return parts.Count > 0 ? string.Join(" | ", parts) + Environment.NewLine : "";
        }
        catch (Exception ex) when (ex is System.IO.IOException or System.Text.Json.JsonException) { return ""; }
    }

    private static double? TryReadRefreshRate(string sessionCsvPath)
    {
        try
        {
            string json = XrPerf.XrPerfRecorder.GetCompanionPath(sessionCsvPath);
            if (!System.IO.File.Exists(json)) return null;
            using var doc = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(json));
            if (doc.RootElement.TryGetProperty("Layer", out var layer) && layer.ValueKind == System.Text.Json.JsonValueKind.Object &&
                layer.TryGetProperty("DisplayRefreshRate", out var hz) && hz.TryGetDouble(out double v) && v > 0)
                return v;
        }
        catch (Exception ex) when (ex is System.IO.IOException or System.Text.Json.JsonException) { }
        return null;
    }

    private void RunFramesAnalysis()
    {
        if (_framesPath is null) return;
        double.TryParse(TxtFramesHz.Text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double hz);
        try
        {
            TxtFramesReport.Text = _hmdInfo + XrPerf.FramesAnalyzer.BuildVerdict(_framesPath, hz) + Environment.NewLine
                + XrPerf.FramesAnalyzer.BuildReport(_framesPath, hz);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            TxtFramesReport.Text = $"Failed to read frames file:\n{ex.Message}";
        }
    }

    private void BtnAnalyzeFrames_Click(object sender, RoutedEventArgs e) => RunFramesAnalysis();

    private void BtnOpenFrames_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Open per-frame CSV",
            Filter = "Frames CSV (*_frames.csv)|*_frames.csv|All files (*.*)|*.*",
            CheckFileExists = true,
        };
        if (dlg.ShowDialog() != true) return;
        _framesPath = dlg.FileName;
        TxtFramesPath.Text = System.IO.Path.GetFileName(_framesPath);
        _hmdInfo = "";
        SetDetectedRefreshRate(null);
        RunFramesAnalysis();
    }

    private void SetDetectedRefreshRate(double? fromSession)
    {
        if (_framesPath is null) return;
        double hz = fromSession ?? XrPerf.FramesAnalyzer.EstimateRefreshRate(_framesPath);
        if (hz > 0)
            TxtFramesHz.Text = Math.Round(hz).ToString(System.Globalization.CultureInfo.InvariantCulture);
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

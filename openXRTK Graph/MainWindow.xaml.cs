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
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        // Initialize
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

        try
        {
            _data = CsvParser.Parse(dlg.FileName);
            if (_data.Count == 0)
            {
                MessageBox.Show("The file contained no valid data rows.", "Empty File", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _meta = CompanionDataLoader.TryLoad(dlg.FileName);
            TxtFilePath.Text = System.IO.Path.GetFileName(dlg.FileName);
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
            _data, "CPU Frametime Distribution – App CPU / Render CPU", 0.2);

        // Frametime Distribution tab – GPU lines
        var gpuDist = ChartBuilder.BuildFrametimeDistribution(_data, "GPU Frametime Distribution – App GPU", 0.2);
        // Keep only App GPU series for the GPU panel
        var appGpuSeries = gpuDist.Series.LastOrDefault();
        gpuDist.Series.Clear();
        if (appGpuSeries is not null)
            gpuDist.Series.Add(appGpuSeries);
        ChartGpuDist.Model = gpuDist;

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

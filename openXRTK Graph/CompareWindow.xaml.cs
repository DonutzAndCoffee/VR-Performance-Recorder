using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace openXRTK_Graph;

public partial class CompareWindow : Window
{
    private const int SlotCount = 3;
    private static readonly string[] Keys = ["A", "B", "C"];

    private readonly CompareSession?[] _sessions = new CompareSession?[SlotCount];

    public CompareWindow()
    {
        InitializeComponent();
    }

    private void BtnOpenA_Click(object sender, RoutedEventArgs e) => LoadSession(0);
    private void BtnOpenB_Click(object sender, RoutedEventArgs e) => LoadSession(1);
    private void BtnOpenC_Click(object sender, RoutedEventArgs e) => LoadSession(2);

    private void BtnClearC_Click(object sender, RoutedEventArgs e)
    {
        _sessions[2] = null;
        TxtFileC.Text = "not loaded";
        StatsBarC.Visibility = Visibility.Collapsed;
        BtnClearC.Visibility = Visibility.Collapsed;
        UpdateCharts();
    }

    private (TextBlock File, TextBlock Stats, FrameworkElement Bar) SlotControls(int slot) => slot switch
    {
        0 => (TxtFileA, TxtStatsA, StatsBarA),
        1 => (TxtFileB, TxtStatsB, StatsBarB),
        _ => (TxtFileC, TxtStatsC, StatsBarC),
    };

    /// <summary>Loads up to three sessions into slots A, B, C (used by the SimHub plugin).</summary>
    public void LoadFiles(IReadOnlyList<string> paths)
    {
        for (int i = 0; i < SlotCount; i++)
        {
            if (i < paths.Count) LoadSessionFile(i, paths[i], refresh: false);
        }
        UpdateCharts();
    }

    private void LoadSession(int slot)
    {
        var dlg = new OpenFileDialog
        {
            Title = $"Open Session {Keys[slot]} CSV",
            Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*",
            CheckFileExists = true,
        };
        if (dlg.ShowDialog() != true) return;
        LoadSessionFile(slot, dlg.FileName, refresh: true);
    }

    private void LoadSessionFile(int slot, string fileName, bool refresh)
    {
        try
        {
            var data  = CsvParser.Parse(fileName);
            if (data.Count == 0)
            {
                MessageBox.Show($"The file contained no valid data rows:\n{fileName}", "Empty File",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            var meta  = CompanionDataLoader.TryLoad(fileName);
            string label = Path.GetFileNameWithoutExtension(fileName);

            _sessions[slot] = new CompareSession(Keys[slot], label, data, meta);
            var (file, stats, bar) = SlotControls(slot);
            file.Text = Path.GetFileName(fileName);
            stats.Text = FormatStats(data);
            bar.Visibility = Visibility.Visible;
            if (slot == 2) BtnClearC.Visibility = Visibility.Visible;

            if (refresh) UpdateCharts();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to load file:\n{ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static string FormatStats(List<CsvDataPoint> data)
    {
        if (data.Count == 0) return "";
        var sortedFps = data.Select(d => d.Fps).OrderBy(f => f).ToList();
        double fps1low = sortedFps[Math.Max(0, (int)(sortedFps.Count * 0.01))];
        return string.Join("   ",
            $"Samples: {data.Count}",
            $"FPS avg: {data.Average(d => d.Fps):F1}",
            $"1%low: {fps1low:F1}",
            $"GPU avg: {data.Average(d => d.AppGpuMs):F2} ms",
            $"CPU avg: {data.Average(d => d.AppCpuMs):F2} ms",
            $"VRAM: {data.Average(d => d.VramMb):F0} MB");
    }

    private void UpdateCharts()
    {
        var loaded = _sessions.OfType<CompareSession>().ToList();
        if (loaded.Count < 2)
        {
            Title = "Session Comparison";
            ChartCompDist.Model    = null;
            ChartCompOverlay.Model = null;
            TxtCompReport.Text     = AnalysisEngine.CompareSessionsAnalysis(loaded);
            return;
        }

        Title = "Session Comparison — " + string.Join("  vs.  ", loaded.Select(s => s.Label));
        ChartCompDist.Model    = ChartBuilder.BuildComparisonDistribution(loaded);
        ChartCompOverlay.Model = ChartBuilder.BuildComparisonFrameIndexSeries(loaded);
        TxtCompReport.Text     = AnalysisEngine.CompareSessionsAnalysis(loaded);
    }
}

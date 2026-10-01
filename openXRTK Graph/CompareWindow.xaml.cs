using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace openXRTK_Graph;

public partial class CompareWindow : Window
{
    private List<CsvDataPoint> _dataA = [];
    private List<CsvDataPoint> _dataB = [];
    private SessionMetadata?   _metaA;
    private SessionMetadata?   _metaB;
    private string _labelA = "";
    private string _labelB = "";

    public CompareWindow()
    {
        InitializeComponent();
    }

    private void BtnOpenA_Click(object sender, RoutedEventArgs e) => LoadSession(isA: true);
    private void BtnOpenB_Click(object sender, RoutedEventArgs e) => LoadSession(isA: false);

    private void LoadSession(bool isA)
    {
        var dlg = new OpenFileDialog
        {
            Title = isA ? "Open Session A CSV" : "Open Session B CSV",
            Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*",
            CheckFileExists = true,
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            var data  = CsvParser.Parse(dlg.FileName);
            if (data.Count == 0)
            {
                MessageBox.Show("The file contained no valid data rows.", "Empty File",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            var meta  = CompanionDataLoader.TryLoad(dlg.FileName);
            string label = Path.GetFileNameWithoutExtension(dlg.FileName);

            if (isA)
            {
                _dataA = data; _metaA = meta; _labelA = label;
                TxtFileA.Text = Path.GetFileName(dlg.FileName);
                TxtStatsA.Text = FormatStats(data);
                StatsBarA.Visibility = Visibility.Visible;
            }
            else
            {
                _dataB = data; _metaB = meta; _labelB = label;
                TxtFileB.Text = Path.GetFileName(dlg.FileName);
                TxtStatsB.Text = FormatStats(data);
                StatsBarB.Visibility = Visibility.Visible;
            }

            if (_dataA.Count > 0 && _dataB.Count > 0)
                UpdateCharts();
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
        Title = $"Session Comparison — {_labelA}  vs.  {_labelB}";
        ChartCompDist.Model    = ChartBuilder.BuildComparisonDistribution(_dataA, _labelA, _dataB, _labelB);
        ChartCompOverlay.Model = ChartBuilder.BuildComparisonFrameIndexSeries(_dataA, _labelA, _dataB, _labelB);
        TxtCompReport.Text     = AnalysisEngine.CompareSessionsAnalysis(_dataA, _metaA, _labelA, _dataB, _metaB, _labelB);
    }
}

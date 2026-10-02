using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using XrPerf.Contracts;

namespace openXRTK_Graph;

/// <summary>Lists recorded sessions (like the SimHub plugin) to open, compare or delete them.</summary>
public partial class SessionsWindow : Window
{
    public SessionsWindow()
    {
        InitializeComponent();
        RefreshSessions();

        if (App.Recorder is { } recorder)
        {
            Action<bool> onChanged = _ => Dispatcher.BeginInvoke(RefreshSessions);
            recorder.RecordingStateChanged += onChanged;
            Closed += (_, _) => recorder.RecordingStateChanged -= onChanged;
        }
    }

    private async void RefreshSessions()
    {
        if (App.Recorder is not { } recorder) return;
        string dir = recorder.SessionsDirectory;
        LstSessions.ItemsSource = await Task.Run(() => XrPerf.XrPerfControlServer.ListSessions(dir, 200));
    }

    private void BtnRefresh_Click(object sender, RoutedEventArgs e) => RefreshSessions();

    private void LstSessions_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        int n = LstSessions.SelectedItems.Count;
        BtnCompare.IsEnabled = n >= 2 && n <= ControlProtocol.MaxCompareSessions;
        BtnDelete.IsEnabled = n > 0;
    }

    private void LstSessions_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (LstSessions.SelectedItem is SessionSummary session)
            ((App)Application.Current).OpenSession(session.Path);
    }

    private void BtnCompare_Click(object sender, RoutedEventArgs e)
    {
        var paths = LstSessions.SelectedItems.OfType<SessionSummary>()
            .OrderBy(s => s.StartTime).Select(s => s.Path).ToList();
        ((App)Application.Current).CompareSessions(paths);
    }

    private void BtnDelete_Click(object sender, RoutedEventArgs e)
    {
        var selected = LstSessions.SelectedItems.OfType<SessionSummary>().ToList();
        if (selected.Count == 0) return;
        string? current = App.Recorder?.GetStatus().CurrentSessionPath;
        if (current != null && selected.Any(s => string.Equals(Path.GetFullPath(s.Path), Path.GetFullPath(current), StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show("The session currently being recorded cannot be deleted.", "Delete sessions", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var files = selected.SelectMany(s => XrPerf.XrPerfControlServer.GetSessionFiles(s.Path)).ToList();
        if (MessageBox.Show($"Delete {selected.Count} session(s) with {files.Count} file(s)?", "Delete sessions",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try
        {
            foreach (var f in files) File.Delete(f);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(ex.Message, "Delete sessions", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        RefreshSessions();
    }
}

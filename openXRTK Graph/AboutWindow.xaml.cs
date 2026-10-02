using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Navigation;

namespace openXRTK_Graph;

/// <summary>About dialog: version, copyright, license (CC BY-NC 4.0) and third-party attributions.</summary>
public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        VersionText.Text = $"Version {UpdateChecker.CurrentVersion.ToString(3)}";
        Loaded += async (_, _) => await CheckForUpdatesAsync();
    }

    private async Task CheckForUpdatesAsync()
    {
        UpdateText.Text = "Checking for updates...";
        try
        {
            var result = await UpdateChecker.CheckAsync();
            UpdateText.Inlines.Clear();
            if (result.NoReleaseYet || result.Latest is null)
            {
                UpdateText.Text = "No published release found on GitHub yet.";
            }
            else if (result.UpdateAvailable)
            {
                UpdateText.Inlines.Add(new Run($"Update available: {result.Latest.ToString(3)} ") { Foreground = Brushes.Orange });
                var link = new Hyperlink(new Run("Download")) { NavigateUri = new Uri(result.ReleaseUrl ?? UpdateChecker.ReleasesUrl) };
                link.RequestNavigate += Hyperlink_RequestNavigate;
                UpdateText.Inlines.Add(link);
            }
            else
            {
                UpdateText.Text = $"You are up to date (latest release: {result.Latest.ToString(3)}).";
            }
        }
        catch (Exception ex)
        {
            UpdateText.Text = $"Update check failed: {ex.Message}";
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }
}

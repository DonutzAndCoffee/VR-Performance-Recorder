using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;

namespace openXRTK_Graph;

/// <summary>Checks the latest GitHub release of the repository against the running assembly version.</summary>
public static class UpdateChecker
{
    public const string ReleasesUrl = "https://github.com/DonutzAndCoffee/openXRTK-Graph/releases";
    private const string LatestReleaseApi = "https://api.github.com/repos/DonutzAndCoffee/openXRTK-Graph/releases/latest";

    public sealed record Result(Version Current, Version? Latest, string? ReleaseUrl, bool NoReleaseYet)
    {
        public bool UpdateAvailable => Latest is not null && Latest > Current;
    }

    public static Version CurrentVersion
    {
        get
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);
            return new Version(v.Major, v.Minor, Math.Max(v.Build, 0));
        }
    }

    public static async Task<Result> CheckAsync(CancellationToken ct = default)
    {
        var current = CurrentVersion;
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("openXRTK-Graph", current.ToString(3)));
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        using var response = await http.GetAsync(LatestReleaseApi, ct).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return new Result(current, null, null, true);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        var root = doc.RootElement;
        var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
        var url = root.TryGetProperty("html_url", out var u) ? u.GetString() : ReleasesUrl;

        return new Result(current, ParseVersion(tag), url, false);
    }

    private static Version? ParseVersion(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        var s = tag.Trim().TrimStart('v', 'V');
        var dash = s.IndexOfAny(['-', '+']);
        if (dash >= 0) s = s[..dash];
        if (!Version.TryParse(s, out var v)) return null;
        return new Version(v.Major, v.Minor, Math.Max(v.Build, 0));
    }
}

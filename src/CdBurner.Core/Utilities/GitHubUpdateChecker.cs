using System.Globalization;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace CdBurner.Core.Utilities;

/// <summary>
/// Result of an opt-in release update check against GitHub.
/// </summary>
/// <param name="IsUpdateAvailable">True if a newer release exists on GitHub than the current version.</param>
/// <param name="CurrentVersion">Current running application version.</param>
/// <param name="LatestVersion">Latest release tag discovered on GitHub.</param>
/// <param name="ReleaseUrl">Web URL to the GitHub release page.</param>
/// <param name="ErrorMessage">Error message if the network request or parsing failed.</param>
public sealed record UpdateCheckResult(
    bool IsUpdateAvailable,
    string CurrentVersion,
    string LatestVersion,
    string ReleaseUrl,
    string? ErrorMessage = null)
{
    /// <summary>
    /// Indicates whether the update check request succeeded without errors.
    /// </summary>
    public bool Success => ErrorMessage == null;
}

/// <summary>
/// Queries the GitHub Releases API for new IgnisDisc updates strictly when explicitly invoked by user opt-in.
/// </summary>
public static class GitHubUpdateChecker
{
    /// <summary>
    /// Default GitHub API endpoint for the latest release.
    /// </summary>
    public const string DefaultReleaseApiUrl = "https://api.github.com/repos/tether-works/IgnisDisc/releases/latest";

    /// <summary>The project's releases page, used when a response carries no usable link.</summary>
    public const string ReleasesPageUrl = "https://github.com/tether-works/IgnisDisc/releases";

    /// <summary>True only for https links under this project's GitHub releases.</summary>
    public static bool IsTrustedReleaseUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps &&
        uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) &&
        uri.AbsolutePath.StartsWith("/tether-works/IgnisDisc/releases", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Resolves the current application version from informational assembly attributes.
    /// </summary>
    public static string GetCurrentAppVersion()
    {
        var infoVer = typeof(GitHubUpdateChecker).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0];
        if (!string.IsNullOrWhiteSpace(infoVer))
        {
            return infoVer;
        }

        var ver = typeof(GitHubUpdateChecker).Assembly.GetName().Version;
        return ver != null ? $"{ver.Major}.{ver.Minor}" : "1.10";
    }

    /// <summary>
    /// Compares two version strings (e.g. "1.10" vs "v1.11") segment by segment numerically.
    /// Returns &lt; 0 if currentVersion &lt; latestTag (update available), 0 if equal, and &gt; 0 if current is newer.
    /// </summary>
    public static int CompareVersions(string currentVersion, string latestTag)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(currentVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(latestTag);

        static int[] ParseVersionSegments(string ver)
        {
            string cleaned = ver.Trim();
            if (cleaned.StartsWith('v') || cleaned.StartsWith('V'))
            {
                cleaned = cleaned[1..].Trim();
            }

            int metaIdx = cleaned.IndexOfAny(new[] { '-', '+' });
            if (metaIdx >= 0)
            {
                cleaned = cleaned[..metaIdx];
            }

            var parts = cleaned.Split('.', StringSplitOptions.RemoveEmptyEntries);
            var segments = new List<int>();
            foreach (var part in parts)
            {
                if (int.TryParse(part, CultureInfo.InvariantCulture, out int val))
                {
                    segments.Add(val);
                }
                else
                {
                    segments.Add(0);
                }
            }

            return segments.ToArray();
        }

        var v1 = ParseVersionSegments(currentVersion);
        var v2 = ParseVersionSegments(latestTag);

        int maxLen = Math.Max(v1.Length, v2.Length);
        for (int i = 0; i < maxLen; i++)
        {
            int seg1 = i < v1.Length ? v1[i] : 0;
            int seg2 = i < v2.Length ? v2[i] : 0;

            if (seg1 != seg2)
            {
                return seg1.CompareTo(seg2);
            }
        }

        return 0;
    }

    /// <summary>
    /// Queries GitHub for the latest release and evaluates whether a newer version is available.
    /// </summary>
    /// <param name="currentVersion">Current application version. When omitted, resolves from assembly metadata.</param>
    /// <param name="httpClient">Injectable <see cref="HttpClient"/> for unit test mocking.</param>
    /// <param name="apiUrl">Custom API endpoint URL. Defaults to tether-works/IgnisDisc latest release.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static async Task<UpdateCheckResult> CheckForUpdateAsync(
        string? currentVersion = null,
        HttpClient? httpClient = null,
        string? apiUrl = null,
        CancellationToken cancellationToken = default)
    {
        string current = currentVersion ?? GetCurrentAppVersion();
        string url = apiUrl ?? DefaultReleaseApiUrl;

        bool disposeClient = false;
        if (httpClient == null)
        {
            httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            disposeClient = true;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd($"IgnisDisc/{current}");
            request.Headers.Accept.ParseAdd("application/vnd.github.v3+json");

            using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return new UpdateCheckResult(
                    IsUpdateAvailable: false,
                    CurrentVersion: current,
                    LatestVersion: string.Empty,
                    ReleaseUrl: string.Empty,
                    ErrorMessage: $"GitHub API returned HTTP {(int)response.StatusCode} ({response.ReasonPhrase}).");
            }

            string json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string tagName = root.TryGetProperty("tag_name", out var tagElem) ? tagElem.GetString() ?? string.Empty : string.Empty;
            string htmlUrl = root.TryGetProperty("html_url", out var urlElem) ? urlElem.GetString() ?? string.Empty : string.Empty;

            // Only ever point the user at this project's own release pages, whatever the response says.
            if (!IsTrustedReleaseUrl(htmlUrl))
            {
                htmlUrl = ReleasesPageUrl;
            }

            if (string.IsNullOrWhiteSpace(tagName))
            {
                return new UpdateCheckResult(
                    IsUpdateAvailable: false,
                    CurrentVersion: current,
                    LatestVersion: string.Empty,
                    ReleaseUrl: htmlUrl,
                    ErrorMessage: "GitHub release response did not contain a valid 'tag_name'.");
            }

            int cmp = CompareVersions(current, tagName);
            bool isUpdateAvailable = cmp < 0;

            return new UpdateCheckResult(
                IsUpdateAvailable: isUpdateAvailable,
                CurrentVersion: current,
                LatestVersion: tagName,
                ReleaseUrl: htmlUrl,
                ErrorMessage: null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new UpdateCheckResult(
                IsUpdateAvailable: false,
                CurrentVersion: current,
                LatestVersion: string.Empty,
                ReleaseUrl: string.Empty,
                ErrorMessage: ex.Message);
        }
        finally
        {
            if (disposeClient)
            {
                httpClient.Dispose();
            }
        }
    }
}

using System.Diagnostics;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;

namespace Unison.Services;

/// <summary>
/// Checks GitHub Releases for a newer Unison-Setup installer and can download/run it.
/// Called from Settings and MainWindow startup. Public repo; no token.
/// </summary>
public sealed class UpdateCheckService
{
    public const string ReleasesLatestUrl = "https://api.github.com/repos/FreezeAnts/Unison/releases/latest";
    public static readonly TimeSpan StartupCheckInterval = TimeSpan.FromHours(12);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly ILogger<UpdateCheckService> _logger;
    private readonly HttpClient _http;

    public UpdateCheckService(ILogger<UpdateCheckService> logger, HttpClient? httpClient = null)
    {
        _logger = logger;
        _http = httpClient ?? new HttpClient();
        if (!_http.DefaultRequestHeaders.UserAgent.Any())
        {
            _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Unison", CurrentVersion));
        }

        _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    public string CurrentVersion
    {
        get
        {
            var informational = Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion;
            var raw = informational ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0";
            var plus = raw.IndexOf('+');
            if (plus >= 0)
            {
                raw = raw[..plus];
            }

            return NormalizeVersion(raw);
        }
    }

    public bool ShouldCheckOnStartup(DateTimeOffset? lastCheckUtc, DateTimeOffset utcNow)
    {
        if (lastCheckUtc is null)
        {
            return true;
        }

        return utcNow - lastCheckUtc.Value >= StartupCheckInterval;
    }

    public async Task<UpdateCheckResult> CheckLatestAsync(CancellationToken cancellationToken = default)
    {
        var current = CurrentVersion;
        try
        {
            using var response = await _http.GetAsync(ReleasesLatestUrl, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var message = response.StatusCode == System.Net.HttpStatusCode.Forbidden
                    ? "GitHub rate limit or access error. Try again later."
                    : $"Could not check for updates ({(int)response.StatusCode}).";
                _logger.LogWarning("Update check failed: {Status} {Body}", response.StatusCode, body);
                return UpdateCheckResult.Error(current, message);
            }

            var release = JsonSerializer.Deserialize<GitHubRelease>(body, JsonOptions);
            var tag = NormalizeVersion(release?.TagName ?? "");
            if (string.IsNullOrWhiteSpace(tag) || !Version.TryParse(PadVersion(tag), out var latestVersion))
            {
                return UpdateCheckResult.Error(current, "Latest GitHub release has no usable version tag.");
            }

            if (!Version.TryParse(PadVersion(current), out var currentVersion))
            {
                currentVersion = new Version(0, 0, 0);
            }

            var asset = release?.Assets?.FirstOrDefault(a =>
                a.Name is not null
                && a.Name.StartsWith("Unison-Setup-", StringComparison.OrdinalIgnoreCase)
                && a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(a.BrowserDownloadUrl));

            if (latestVersion <= currentVersion)
            {
                _logger.LogInformation("Unison {Current} is up to date (latest {Latest}).", current, tag);
                return new UpdateCheckResult(false, current, tag, null, null, null);
            }

            if (asset is null)
            {
                return UpdateCheckResult.Error(current, $"Update {tag} is on GitHub but has no Unison-Setup installer.");
            }

            _logger.LogInformation("Update available: {Current} -> {Latest} ({Asset}).", current, tag, asset.Name);
            return new UpdateCheckResult(true, current, tag, asset.BrowserDownloadUrl, asset.Name, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Update check failed.");
            return UpdateCheckResult.Error(current, "Could not reach GitHub. Check your network and try again.");
        }
    }

    public async Task<string?> DownloadInstallerAsync(UpdateCheckResult update, IProgress<string>? status, CancellationToken cancellationToken = default)
    {
        if (!update.UpdateAvailable || string.IsNullOrWhiteSpace(update.DownloadUrl) || string.IsNullOrWhiteSpace(update.AssetName))
        {
            return null;
        }

        var dest = Path.Combine(Path.GetTempPath(), update.AssetName);
        status?.Report("Downloading installer…");
        _logger.LogInformation("Downloading {Url} to {Path}.", update.DownloadUrl, dest);

        using var response = await _http.GetAsync(update.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var output = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None);
        await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
        return dest;
    }

    public void LaunchInstallerAndExit(string setupPath)
    {
        _logger.LogInformation("Launching installer {Path} and exiting Unison.", setupPath);
        Process.Start(new ProcessStartInfo
        {
            FileName = setupPath,
            UseShellExecute = true
        });
        Application.Current.Exit();
    }

    private static string NormalizeVersion(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.StartsWith("v", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[1..];
        }

        return trimmed;
    }

    private static string PadVersion(string version)
    {
        var parts = version.Split('.', StringSplitOptions.RemoveEmptyEntries);
        while (parts.Length < 3)
        {
            version += ".0";
            parts = version.Split('.');
        }

        return string.Join('.', parts.Take(3));
    }

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; set; }

        [JsonPropertyName("assets")]
        public List<GitHubAsset>? Assets { get; set; }
    }

    private sealed class GitHubAsset
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("browser_download_url")]
        public string? BrowserDownloadUrl { get; set; }
    }
}

public sealed record UpdateCheckResult(
    bool UpdateAvailable,
    string CurrentVersion,
    string? LatestVersion,
    string? DownloadUrl,
    string? AssetName,
    string? ErrorMessage)
{
    public static UpdateCheckResult Error(string current, string message) =>
        new(false, current, null, null, null, message);
}

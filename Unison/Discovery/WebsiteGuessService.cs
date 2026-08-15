using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Unison.Discovery;

/// <summary>
/// Guesses an official website from a free-text query via DuckDuckGo Instant Answer.
/// Called by AddServiceViewModel. Not an allowlist; empty results are fine.
/// </summary>
public sealed class WebsiteGuessService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<WebsiteGuessService> _logger;

    public WebsiteGuessService(ILogger<WebsiteGuessService> logger)
    {
        _logger = logger;
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Unison/0.1");
    }

    public async Task<Uri?> GuessOfficialSiteAsync(string query, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Length < 2)
        {
            return null;
        }

        try
        {
            var url =
                "https://api.duckduckgo.com/?q=" + Uri.EscapeDataString(query.Trim())
                + "&format=json&no_html=1&no_redirect=1&skip_disambig=1";
            using var response = await _httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            var root = doc.RootElement;
            foreach (var candidate in EnumerateUrlCandidates(root))
            {
                if (TryHttpUri(candidate, out var uri))
                {
                    return uri;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "DuckDuckGo guess failed for {Query}.", query);
        }

        return null;
    }

    private static IEnumerable<string> EnumerateUrlCandidates(JsonElement root)
    {
        if (root.TryGetProperty("AbstractURL", out var abstractUrl))
        {
            yield return abstractUrl.GetString() ?? string.Empty;
        }

        if (root.TryGetProperty("OfficialWebsite", out var official))
        {
            yield return official.GetString() ?? string.Empty;
        }

        if (root.TryGetProperty("Redirect", out var redirect))
        {
            yield return redirect.GetString() ?? string.Empty;
        }

        if (root.TryGetProperty("Results", out var results) && results.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in results.EnumerateArray())
            {
                if (item.TryGetProperty("FirstURL", out var first))
                {
                    yield return first.GetString() ?? string.Empty;
                }
            }
        }

        if (root.TryGetProperty("RelatedTopics", out var related) && related.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in related.EnumerateArray())
            {
                if (item.TryGetProperty("FirstURL", out var first))
                {
                    yield return first.GetString() ?? string.Empty;
                }
            }
        }
    }

    private static bool TryHttpUri(string? value, out Uri uri)
    {
        uri = null!;
        if (string.IsNullOrWhiteSpace(value)
            || !Uri.TryCreate(value, UriKind.Absolute, out var parsed)
            || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps)
            || parsed.Host.Contains("duckduckgo.com", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        uri = parsed;
        return true;
    }
}

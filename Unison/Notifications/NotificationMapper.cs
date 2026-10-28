using Unison.Models;

namespace Unison.Notifications;

/// <summary>
/// Maps a Windows toast to a configured Unison service using name, process, and URL host.
/// Called by NotificationManager. Does not use a hardcoded service-id table.
/// Mapping is independent of ShowNotificationBadge so auto-switch still works when badges are off.
/// </summary>
public sealed class NotificationMapper
{
    public string? MapToServiceId(
        string appUserModelId,
        string displayName,
        IReadOnlyList<ServiceDefinition> services)
    {
        string? bestId = null;
        var bestScore = 0;

        foreach (var service in services)
        {
            var score = ScoreMatch(appUserModelId, displayName, service);
            if (score > bestScore)
            {
                bestScore = score;
                bestId = service.Id;
            }
        }

        return bestScore > 0 ? bestId : null;
    }

    private static int ScoreMatch(string appUserModelId, string displayName, ServiceDefinition service)
    {
        var score = 0;

        if (!string.IsNullOrWhiteSpace(service.NotificationAppId))
        {
            if (ContainsToken(appUserModelId, service.NotificationAppId)
                || ContainsToken(displayName, service.NotificationAppId))
            {
                score = Math.Max(score, 100);
            }
        }

        if (!string.IsNullOrWhiteSpace(service.Name))
        {
            if (ContainsToken(displayName, service.Name))
            {
                score = Math.Max(score, 90);
            }

            if (ContainsToken(appUserModelId, service.Name))
            {
                score = Math.Max(score, 80);
            }

            foreach (var part in service.Name.Split([' ', '-', '_'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (part.Length >= 3
                    && (ContainsToken(displayName, part) || ContainsToken(appUserModelId, part)))
                {
                    score = Math.Max(score, 60);
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(service.ProcessName))
        {
            foreach (var part in service.ProcessName.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (IsWeakHostToken(part))
                {
                    continue;
                }

                if (part.Length >= 3
                    && (ContainsToken(appUserModelId, part) || ContainsToken(displayName, part)))
                {
                    score = Math.Max(score, 70);
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(service.Url)
            && Uri.TryCreate(service.Url, UriKind.Absolute, out var uri)
            && !string.IsNullOrWhiteSpace(uri.Host))
        {
            var host = uri.Host.Replace("www.", "", StringComparison.OrdinalIgnoreCase);
            if (ContainsToken(appUserModelId, host) || ContainsToken(displayName, host))
            {
                score = Math.Max(score, 65);
            }

            var label = host.Split('.')[0];
            if (label.Length >= 3
                && (ContainsToken(appUserModelId, label) || ContainsToken(displayName, label)))
            {
                score = Math.Max(score, 55);
            }
        }

        return score;
    }

    private static bool ContainsToken(string haystack, string token)
    {
        if (string.IsNullOrWhiteSpace(haystack) || string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        return haystack.Contains(token, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsWeakHostToken(string part) =>
        part.Equals("ApplicationFrameHost", StringComparison.OrdinalIgnoreCase)
        || part.Equals("msedge", StringComparison.OrdinalIgnoreCase)
        || part.Equals("msedgewebview2", StringComparison.OrdinalIgnoreCase)
        || part.Equals("app", StringComparison.OrdinalIgnoreCase);

    public static bool LooksLikeCall(string title, string body)
    {
        var text = $"{title} {body}";
        return ContainsCallWord(text);
    }

    public static bool TitleLooksLikeCall(string title) => ContainsCallWord(title);

    public static int? TryParseUnreadFromTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        var trimmed = title.TrimStart();
        if (trimmed.StartsWith('('))
        {
            var end = trimmed.IndexOf(')');
            if (end > 1 && int.TryParse(trimmed[1..end], out var parenCount))
            {
                return parenCount;
            }
        }

        var split = trimmed.Split([' ', '|', '•', '-', '—'], 2, StringSplitOptions.RemoveEmptyEntries);
        if (split.Length >= 1 && int.TryParse(split[0], out var prefixCount))
        {
            return prefixCount;
        }

        var searchFrom = 0;
        while (searchFrom < trimmed.Length)
        {
            var open = trimmed.IndexOf('(', searchFrom);
            if (open < 0)
            {
                break;
            }

            var close = trimmed.IndexOf(')', open + 1);
            if (close < 0)
            {
                break;
            }

            var inner = trimmed[(open + 1)..close];
            if (int.TryParse(inner, out var nested)
                && nested is >= 0 and <= 9999
                && nested is < 1900 or > 2100)
            {
                return nested;
            }

            searchFrom = open + 1;
        }

        return null;
    }

    private static bool ContainsCallWord(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        return text.Contains("incoming call", StringComparison.OrdinalIgnoreCase)
            || text.Contains("is calling", StringComparison.OrdinalIgnoreCase)
            || text.Contains("incoming video", StringComparison.OrdinalIgnoreCase)
            || HasWord(text, "call")
            || HasWord(text, "calling")
            || HasWord(text, "meet")
            || HasWord(text, "meeting")
            || HasWord(text, "ringing");
    }

    private static bool HasWord(string text, string word)
    {
        var start = 0;
        while (start < text.Length)
        {
            var index = text.IndexOf(word, start, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                return false;
            }

            var beforeOk = index == 0 || !char.IsLetterOrDigit(text[index - 1]);
            var afterIndex = index + word.Length;
            var afterOk = afterIndex >= text.Length || !char.IsLetterOrDigit(text[afterIndex]);
            if (beforeOk && afterOk)
            {
                return true;
            }

            start = index + 1;
        }

        return false;
    }

    /// <summary>Seed NotificationAppId from the service display name when missing.</summary>
    public static bool TryBackfillNotificationAppId(ServiceDefinition definition)
    {
        if (!string.IsNullOrWhiteSpace(definition.NotificationAppId)
            || string.IsNullOrWhiteSpace(definition.Name))
        {
            return false;
        }

        definition.NotificationAppId = definition.Name.Trim();
        return true;
    }
}

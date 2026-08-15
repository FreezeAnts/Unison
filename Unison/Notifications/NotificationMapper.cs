using Unison.Models;

namespace Unison.Notifications;

/// <summary>
/// Maps a Windows toast to a configured Unison service using name, process, and URL host.
/// Called by NotificationManager. Does not use a hardcoded service-id table.
/// </summary>
public sealed class NotificationMapper
{
    public string? MapToServiceId(
        string appUserModelId,
        string displayName,
        IReadOnlyList<ServiceDefinition> services)
    {
        foreach (var service in services)
        {
            if (!service.ShowNotificationBadge)
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(service.NotificationAppId)
                && appUserModelId.Contains(service.NotificationAppId, StringComparison.OrdinalIgnoreCase))
            {
                return service.Id;
            }

            if (Matches(appUserModelId, service) || Matches(displayName, service))
            {
                return service.Id;
            }
        }

        return null;
    }

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

        return 0;
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
            || HasWord(text, "meeting");
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

    private static bool Matches(string haystack, ServiceDefinition service)
    {
        if (string.IsNullOrWhiteSpace(haystack))
        {
            return false;
        }

        if (haystack.Contains(service.Name, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(service.ProcessName)
            && haystack.Contains(service.ProcessName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(service.Url)
            && Uri.TryCreate(service.Url, UriKind.Absolute, out var uri)
            && !string.IsNullOrWhiteSpace(uri.Host))
        {
            var host = uri.Host.Replace("www.", "", StringComparison.OrdinalIgnoreCase);
            if (haystack.Contains(host, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var label = host.Split('.')[0];
            if (label.Length >= 3 && haystack.Contains(label, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}

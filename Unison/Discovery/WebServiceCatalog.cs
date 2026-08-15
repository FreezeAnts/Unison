using Unison.Models;

namespace Unison.Discovery;

/// <summary>
/// Built-in list of popular web services for the Add Service dialog.
/// Called by AddServiceViewModel. Search matches name, aliases, and URL.
/// </summary>
public sealed class WebServiceCatalog
{
    public IReadOnlyList<WebServiceCatalogEntry> GetAll() =>
    [
        new("gmail", "Gmail", "https://mail.google.com", ["google mail", "email"], "https://www.gstatic.com/images/branding/product/2x/gmail_2020q4_96dp.png"),
        new("google-meet", "Google Meet", "https://meet.google.com", ["meet", "meets", "hangouts meet", "video call"], "https://www.gstatic.com/images/branding/product/2x/meet_2020q4_96dp.png"),
        new("google-chat", "Google Chat", "https://mail.google.com/chat", ["chat", "hangouts", "google hangouts"], "https://fonts.gstatic.com/s/i/productlogos/chat_2020q4/v6/web-96dp/logo_chat_2020q4_color_2x_web_96dp.png"),
        new("google-calendar", "Google Calendar", "https://calendar.google.com", ["calendar", "gcal"], "https://fonts.gstatic.com/s/i/productlogos/calendar_2020q4/v13/web-96dp/logo_calendar_2020q4_color_2x_web_96dp.png"),
        new("google-messages", "Google Messages", "https://messages.google.com/web", ["messages", "android messages", "rcs", "sms"], "https://www.gstatic.com/android-messages-web/images/2022.3/2x/messages_2022_96dp.png"),
        new("bitwarden", "Bitwarden", "https://vault.bitwarden.com", ["password", "passwords"], "https://bitwarden.com/icons/icon-192x192.png", "9PJSDV0VPK04", true),
        new("onepassword", "1Password", "https://my.1password.com", ["1 password", "one password", "onepass", "one pass"], "https://app.1password.com/images/apple-touch-icon-iphone-3x.png", "XP8999320QB46S", true),
        new("home-assistant", "Home Assistant", "http://homeassistant.local:8123", ["hass", "ha", "homeassistant", "home assistant"], "https://brands.home-assistant.io/homeassistant/icon.png"),
        new("outlook-web", "Outlook Web", "https://outlook.office.com/mail", ["o365", "office 365"], "https://res.cdn.office.net/files/fabric-cdn-prod_20240129.001/assets/brand-icons/product/png/outlook_96x1.png", "9NRX63209R7B", true),
        new("slack", "Slack", "https://app.slack.com/client", [], "https://a.slack-edge.com/80588/marketing/img/icons/icon_slack_hash_colored.png"),
        new("discord", "Discord", "https://discord.com/app", [], "https://cdn.prod.website-files.com/6257adef93867e50d84d30e2/636e0a6a49cf127bf92de1e2_icon_clyde_blurple_RGB.png", null, true),
        new("whatsapp", "WhatsApp", "https://web.whatsapp.com", [], "https://web.whatsapp.com/apple-touch-icon.png", "9NKSQGP7F2NH", true),
        new("teams-web", "Teams Web", "https://teams.microsoft.com", [], "https://res.cdn.office.net/files/fabric-cdn-prod_20240129.001/assets/brand-icons/product/png/teams_96x1.png", "XP8BT8DW290MPQ", true),
        new("telegram", "Telegram", "https://web.telegram.org", [], "https://telegram.org/img/t_logo_2x.png", null, true),
        new("messenger", "Messenger", "https://www.messenger.com", ["facebook messenger"], Favicon("www.messenger.com")),
        new("chatgpt", "ChatGPT", "https://chatgpt.com", ["openai"], "https://www.google.com/s2/favicons?sz=256&domain=chatgpt.com"),
        new("github", "GitHub", "https://github.com", [], "https://github.com/fluidicon.png"),
        new("notion", "Notion", "https://www.notion.so", [], "https://www.notion.so/images/logo-ios.png")
    ];

    public string? FindIconUrl(string serviceId) =>
        GetAll().FirstOrDefault(e => e.Id.Equals(serviceId, StringComparison.OrdinalIgnoreCase))?.IconUrl;

    public string? FindIconUrlByName(string name) =>
        GetAll().FirstOrDefault(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.IconUrl;

    private static string Favicon(string domain) =>
        $"https://www.google.com/s2/favicons?sz=128&domain={domain}";
}

public sealed record WebServiceCatalogEntry(
    string Id,
    string Name,
    string Url,
    IReadOnlyList<string> Aliases,
    string? IconUrl,
    string? StoreProductId = null,
    bool OfferStoreApp = false)
{
    public bool ShowsGetApp => OfferStoreApp || !string.IsNullOrWhiteSpace(StoreProductId);

    public Uri CreateStoreUri()
    {
        if (!string.IsNullOrWhiteSpace(StoreProductId))
        {
            return new Uri("ms-windows-store://pdp/?ProductId=" + StoreProductId);
        }

        return new Uri("ms-windows-store://search/?query=" + Uri.EscapeDataString(Name));
    }

    public bool Matches(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return true;
        }

        if (Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)
            || Url.Contains(query, StringComparison.OrdinalIgnoreCase)
            || Id.Contains(query, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return Aliases.Any(alias => alias.Contains(query, StringComparison.CurrentCultureIgnoreCase)
            || query.Contains(alias, StringComparison.CurrentCultureIgnoreCase));
    }

    public ServiceDefinition ToDefinition() => new()
    {
        Id = Id,
        Name = Name,
        ServiceType = ServiceType.WebService,
        Url = Url,
        IconUrl = IconUrl,
        ShowNotificationBadge = true
    };
}

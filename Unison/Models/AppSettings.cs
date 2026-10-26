namespace Unison.Models;

/// <summary>
/// App-wide preferences. Stored in settings.json. Service rows keep their own notification flags.
/// </summary>
public sealed class AppSettings
{
    public AppTheme Theme { get; set; } = AppTheme.System;

    public bool ShowNotificationBadgeByDefault { get; set; } = true;

    /// <summary>
    /// When true, Unison switches to a mapped service on an incoming-call toast.
    /// Also seeds <see cref="ServiceDefinition.AutoSwitchOnNotification"/> for newly added services.
    /// </summary>
    public bool AutoSwitchOnCallByDefault { get; set; } = true;

    public bool MuteOthersDuringCalls { get; set; }

    public ServiceBarPlacement ServiceBarPlacement { get; set; } = ServiceBarPlacement.Sidebar;

    public bool CheckForUpdatesOnStartup { get; set; } = true;

    public DateTimeOffset? LastUpdateCheckUtc { get; set; }

    public void ApplyDefaultsTo(ServiceDefinition definition)
    {
        definition.ShowNotificationBadge = ShowNotificationBadgeByDefault;
        definition.AutoSwitchOnNotification = AutoSwitchOnCallByDefault;
    }
}

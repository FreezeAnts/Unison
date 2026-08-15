namespace Unison.Models;

/// <summary>
/// App-wide preferences. Stored in settings.json. Service rows keep their own notification flags.
/// </summary>
public sealed class AppSettings
{
    public AppTheme Theme { get; set; } = AppTheme.System;

    public bool ShowNotificationBadgeByDefault { get; set; } = true;

    public bool AutoSwitchOnCallByDefault { get; set; }

    public bool MuteOthersDuringCalls { get; set; }

    public ServiceBarPlacement ServiceBarPlacement { get; set; } = ServiceBarPlacement.Sidebar;

    public void ApplyDefaultsTo(ServiceDefinition definition)
    {
        definition.ShowNotificationBadge = ShowNotificationBadgeByDefault;
        definition.AutoSwitchOnNotification = AutoSwitchOnCallByDefault;
    }
}

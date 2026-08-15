namespace Unison.Models;

/// <summary>
/// How a service reacts to a Windows notification. Messages default to badge-only (no focus steal).
/// </summary>
public enum NotificationSwitchMode
{
    BadgeOnly,
    AutoSwitchWhenIdle,
    AlwaysAutoSwitch,
    NeverAutoSwitch
}

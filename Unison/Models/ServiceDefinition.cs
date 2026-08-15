namespace Unison.Models;

/// <summary>
/// Describes a configured service (native app or web).
/// Stored by persistence and passed to adapters. UI binds to ViewModels, not this type directly.
/// </summary>
public sealed class ServiceDefinition
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? IconPath { get; set; }
    public string? IconUrl { get; set; }
    public ServiceType ServiceType { get; set; }
    public string? ExecutablePath { get; set; }
    public string? ProcessName { get; set; }
    public string? Url { get; set; }
    public string? NotificationAppId { get; set; }
    public bool AutoSwitchOnNotification { get; set; }
    public bool ShowNotificationBadge { get; set; } = true;
}

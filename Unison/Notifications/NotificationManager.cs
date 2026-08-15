using Microsoft.Extensions.Logging;
using Unison.Models;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace Unison.Notifications;

/// <summary>
/// Listens for Windows toast notifications, maps them to services, and reports badge counts.
/// Called from MainWindow after launch. Does not steal focus; calls are logged for future auto-switch.
/// </summary>
public sealed class NotificationManager
{
    private readonly NotificationMapper _mapper = new();
    private readonly ILogger<NotificationManager> _logger;
    private Func<IReadOnlyList<ServiceDefinition>> _services = () => [];
    private Action<IReadOnlyDictionary<string, int>, string?, bool>? _onUpdate;
    private UserNotificationListener? _listener;

    public NotificationManager(ILogger<NotificationManager> logger)
    {
        _logger = logger;
    }

    public async Task StartAsync(
        Func<IReadOnlyList<ServiceDefinition>> services,
        Action<IReadOnlyDictionary<string, int>, string?, bool> onUpdate)
    {
        _services = services;
        _onUpdate = onUpdate;

        try
        {
            _listener = UserNotificationListener.Current;
            var access = await _listener.RequestAccessAsync();
            if (access != UserNotificationListenerAccessStatus.Allowed)
            {
                _logger.LogWarning("Notification access is {Access}. Badges will stay at zero until access is granted.", access);
                return;
            }

            _listener.NotificationChanged += ListenerOnNotificationChanged;
            await RefreshAsync().ConfigureAwait(true);
            _logger.LogInformation("NotificationManager is listening for Windows toasts.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not start UserNotificationListener. Unpackaged apps may need notification access in Windows settings.");
        }
    }

    public void Stop()
    {
        if (_listener is not null)
        {
            _listener.NotificationChanged -= ListenerOnNotificationChanged;
        }
    }

    private async void ListenerOnNotificationChanged(UserNotificationListener sender, UserNotificationChangedEventArgs args)
    {
        try
        {
            await RefreshAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to refresh notifications.");
        }
    }

    private async Task RefreshAsync()
    {
        if (_listener is null)
        {
            return;
        }

        IReadOnlyList<UserNotification> toasts;
        try
        {
            toasts = await _listener.GetNotificationsAsync(NotificationKinds.Toast);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read Windows toast notifications.");
            return;
        }

        var services = _services();
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        string? latestServiceId = null;
        var latestIsCall = false;

        foreach (var toast in toasts)
        {
            string aumid;
            string displayName;
            try
            {
                var appInfo = toast.AppInfo;
                aumid = appInfo.AppUserModelId ?? string.Empty;
                displayName = appInfo.DisplayInfo?.DisplayName ?? string.Empty;
            }
            catch
            {
                continue;
            }

            var serviceId = _mapper.MapToServiceId(aumid, displayName, services);
            if (serviceId is null)
            {
                continue;
            }

            counts[serviceId] = counts.GetValueOrDefault(serviceId) + 1;
            var (title, body) = ReadText(toast);
            latestIsCall = NotificationMapper.LooksLikeCall(title, body);
            latestServiceId = serviceId;
            _logger.LogDebug(
                "Mapped notification from {Display} ({Aumid}) to {ServiceId}. Call={IsCall}.",
                displayName,
                aumid,
                serviceId,
                latestIsCall);
        }

        _onUpdate?.Invoke(counts, latestServiceId, latestIsCall);
    }

    private static (string Title, string Body) ReadText(UserNotification toast)
    {
        try
        {
            var binding = toast.Notification.Visual?.GetBinding(KnownNotificationBindings.ToastGeneric);
            var text = binding?.GetTextElements();
            if (text is null || text.Count == 0)
            {
                return (string.Empty, string.Empty);
            }

            var title = text[0].Text ?? string.Empty;
            var body = text.Count > 1 ? text[1].Text ?? string.Empty : string.Empty;
            return (title, body);
        }
        catch
        {
            return (string.Empty, string.Empty);
        }
    }
}

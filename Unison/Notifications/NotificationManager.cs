using Microsoft.Extensions.Logging;
using Unison.Models;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace Unison.Notifications;

/// <summary>
/// Listens for Windows toast notifications, maps them to services, and reports badge counts.
/// Called from MainWindow after launch. Passes the newest mapped toast so MainViewModel can auto-switch.
/// </summary>
public sealed class NotificationManager
{
    private readonly NotificationMapper _mapper = new();
    private readonly ILogger<NotificationManager> _logger;
    private Func<IReadOnlyList<ServiceDefinition>> _services = () => [];
    private Action<IReadOnlyDictionary<string, int>, string?, bool>? _onUpdate;
    private UserNotificationListener? _listener;
    private readonly Dictionary<string, int> _claimed = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<uint> _seenToastIds = [];

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

    public void Acknowledge(string serviceId)
    {
        _claimed.Remove(serviceId);
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
        var live = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        // Only a newly seen toast should trigger auto-switch. Re-scans of existing
        // history must not yank the user to another tab.
        string? newServiceId = null;
        var newIsCall = false;
        var mapped = 0;
        var cleared = 0;

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

            mapped++;
            live[serviceId] = live.GetValueOrDefault(serviceId) + 1;
            var (title, body) = ReadText(toast);
            var isCall = NotificationMapper.LooksLikeCall(title, body);

            if (_seenToastIds.Add(toast.Id))
            {
                _claimed[serviceId] = _claimed.GetValueOrDefault(serviceId) + 1;
                // Prefer a call toast if several arrive in one batch.
                if (newServiceId is null || isCall)
                {
                    newServiceId = serviceId;
                    newIsCall = isCall;
                }

                if (TryTakeOverToast(aumid))
                {
                    cleared++;
                }
            }
        }

        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in live.Keys.Concat(_claimed.Keys).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            counts[id] = Math.Max(live.GetValueOrDefault(id), _claimed.GetValueOrDefault(id));
        }

        if (newServiceId is not null)
        {
            _logger.LogInformation(
                "New toast for {ServiceId} (call={IsCall}); badges updated for {Count} service(s).",
                newServiceId,
                newIsCall,
                counts.Count);
        }

        _onUpdate?.Invoke(counts, newServiceId, newIsCall);
    }

    private static bool TryTakeOverToast(string aumid)
    {
        if (string.IsNullOrWhiteSpace(aumid))
        {
            return false;
        }

        try
        {
            ToastNotificationManager.History.Clear(aumid);
            return true;
        }
        catch
        {
            return false;
        }
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

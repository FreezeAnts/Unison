using Microsoft.Extensions.Logging;
using Unison.Models;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace Unison.Notifications;

/// <summary>
/// Listens for Windows toast notifications, maps them to services, and reports badge counts.
/// Called from MainWindow after launch. Passes the newest mapped toast so MainViewModel can auto-switch.
/// Does not clear Action Center history — clearing raced the listener and dropped switch/badge updates.
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
    private readonly HashSet<uint> _ignoredForBadge = [];
    private readonly Dictionary<uint, string> _toastService = [];
    private bool _accessDenied;

    public NotificationManager(ILogger<NotificationManager> logger)
    {
        _logger = logger;
    }

    /// <summary>True when Windows denied Unison access to toast history.</summary>
    public bool AccessDenied => _accessDenied;

    /// <summary>Human-readable access status for Settings.</summary>
    public string AccessStatusText =>
        _accessDenied
            ? "Allow Unison in Windows Settings → System → Notifications so badges and auto-switch can work."
            : (_listener is null
                ? "Notification listener is not running."
                : "Listening for Windows toast notifications.");

    public async Task StartAsync(
        Func<IReadOnlyList<ServiceDefinition>> services,
        Action<IReadOnlyDictionary<string, int>, string?, bool> onUpdate)
    {
        _services = services;
        _onUpdate = onUpdate;
        _accessDenied = false;

        try
        {
            _listener = UserNotificationListener.Current;
            var access = await _listener.RequestAccessAsync();
            if (access != UserNotificationListenerAccessStatus.Allowed)
            {
                _accessDenied = true;
                _logger.LogWarning("Notification access is {Access}. Badges will stay at zero until access is granted.", access);
                return;
            }

            _listener.NotificationChanged += ListenerOnNotificationChanged;
            await RefreshAsync().ConfigureAwait(true);
            _logger.LogInformation("NotificationManager is listening for Windows toasts.");
        }
        catch (Exception ex)
        {
            _accessDenied = true;
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

    /// <summary>
    /// User opened this service tab: clear its claimed count and ignore currently visible
    /// toasts for badge purposes so Action Center leftovers do not revive the bubble.
    /// New toasts after this still count and can auto-switch.
    /// </summary>
    public void Acknowledge(string serviceId)
    {
        _claimed.Remove(serviceId);
        foreach (var pair in _toastService.ToList())
        {
            if (pair.Value.Equals(serviceId, StringComparison.OrdinalIgnoreCase))
            {
                _ignoredForBadge.Add(pair.Key);
            }
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
        var live = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var liveToastIds = new HashSet<uint>();
        // Only a newly seen toast should trigger auto-switch. Re-scans of existing
        // history must not yank the user to another tab.
        string? newServiceId = null;
        var newIsCall = false;
        var mapped = 0;

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
            liveToastIds.Add(toast.Id);
            _toastService[toast.Id] = serviceId;

            var (title, body) = ReadText(toast);
            var isCall = NotificationMapper.LooksLikeCall(title, body);
            var isNew = _seenToastIds.Add(toast.Id);

            if (isNew)
            {
                // Brand-new toasts always count for badges, even if this service was acknowledged.
                _ignoredForBadge.Remove(toast.Id);
                _claimed[serviceId] = _claimed.GetValueOrDefault(serviceId) + 1;
                if (newServiceId is null || isCall)
                {
                    newServiceId = serviceId;
                    newIsCall = isCall;
                }

                _logger.LogInformation(
                    "Mapped toast {ToastId} from '{Display}' / '{Aumid}' → {ServiceId} (call={IsCall}).",
                    toast.Id,
                    displayName,
                    aumid,
                    serviceId,
                    isCall);
            }

            if (!_ignoredForBadge.Contains(toast.Id))
            {
                live[serviceId] = live.GetValueOrDefault(serviceId) + 1;
            }
        }

        // Drop tracking for toasts that left Action Center.
        foreach (var id in _toastService.Keys.Where(id => !liveToastIds.Contains(id)).ToList())
        {
            _toastService.Remove(id);
            _ignoredForBadge.Remove(id);
        }

        // Live Action Center count (minus acknowledged) is authoritative while toasts remain;
        // claimed is a floor after the user dismisses them from Action Center.
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in live.Keys.Concat(_claimed.Keys).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            counts[id] = Math.Max(live.GetValueOrDefault(id), _claimed.GetValueOrDefault(id));
        }

        if (newServiceId is not null)
        {
            _logger.LogInformation(
                "New toast for {ServiceId} (call={IsCall}); badges updated for {Count} service(s), mapped={Mapped}.",
                newServiceId,
                newIsCall,
                counts.Count,
                mapped);
        }

        _onUpdate?.Invoke(counts, newServiceId, newIsCall);
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

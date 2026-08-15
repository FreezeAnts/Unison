using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Unison.Models;
using Unison.Notifications;
using Unison.Persistence;
using Unison.Services;
using Unison.Services.Web;

namespace Unison.ViewModels;

/// <summary>
/// Sidebar selection and placeholder content text.
/// Bound by MainWindow. Loads services from ServiceConfigurationStore and switches via ServiceManager.
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly ServiceManager _serviceManager;
    private readonly ServiceConfigurationStore _store;
    private readonly IconLoader _iconLoader;
    private readonly IWebViewHost _webViewHost;
    private readonly ILogger<MainViewModel> _logger;
    private readonly Dictionary<string, int> _toastCounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _titleCounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _titles = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, bool> _playingAudio = new(StringComparer.OrdinalIgnoreCase);

    public MainViewModel(
        ServiceConfigurationStore store,
        ServiceManager serviceManager,
        IconLoader iconLoader,
        IWebViewHost webViewHost,
        ILogger<MainViewModel> logger)
    {
        _store = store;
        _serviceManager = serviceManager;
        _iconLoader = iconLoader;
        _webViewHost = webViewHost;
        _logger = logger;
        _webViewHost.DocumentTitleChanged += OnWebTitleChanged;
        _webViewHost.DocumentAudioChanged += OnWebAudioChanged;

        foreach (var definition in store.Load())
        {
            _serviceManager.Register(definition);
            Services.Add(new ServiceItemViewModel(definition));
        }

        StatusText = "Select a service";
        PlaceholderText = "Select a service, or add a web service to open it here.";
        ShowPlaceholder = true;
        _ = LoadIconsAsync();
    }

    public ObservableCollection<ServiceItemViewModel> Services { get; } = [];

    [ObservableProperty]
    private ServiceItemViewModel? _selectedService;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private string _placeholderText = string.Empty;

    [ObservableProperty]
    private bool _showPlaceholder = true;

    [RelayCommand]
    private async Task SelectServiceAsync(ServiceItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        foreach (var service in Services)
        {
            service.IsSelected = service == item;
        }

        SelectedService = item;
        StatusText = item.Name;
        ShowPlaceholder = false;
        PlaceholderText = string.Empty;
        _logger.LogInformation("User selected {ServiceId}.", item.Definition.Id);
        await _serviceManager.SelectAsync(item.Definition.Id).ConfigureAwait(true);
        RefreshBadges();
        RefreshCallMute();
    }

    public IReadOnlyList<ServiceDefinition> ConfiguredServices =>
        Services.Select(s => s.Definition).ToList();

    public bool TryAddService(ServiceDefinition definition)
    {
        if (Services.Any(s =>
                string.Equals(s.Definition.Id, definition.Id, StringComparison.OrdinalIgnoreCase)
                || (string.Equals(s.Name, definition.Name, StringComparison.OrdinalIgnoreCase)
                    && s.Definition.ServiceType == definition.ServiceType)))
        {
            _logger.LogInformation("Service {Name} is already in the sidebar.", definition.Name);
            return false;
        }

        _serviceManager.Register(definition);
        var item = new ServiceItemViewModel(definition);
        Services.Add(item);
        _store.Save(ConfiguredServices);
        _logger.LogInformation("Added service {ServiceId} ({Name}).", definition.Id, definition.Name);
        _ = LoadIconAndPersistAsync(item);
        return true;
    }

    public void PersistOrder()
    {
        _store.Save(ConfiguredServices);
    }

    public async Task RemoveServiceAsync(ServiceItemViewModel item)
    {
        await _serviceManager.RemoveAsync(item.Definition.Id).ConfigureAwait(true);
        Services.Remove(item);
        if (SelectedService == item)
        {
            SelectedService = null;
            ShowPlaceholder = true;
            StatusText = "Select a service";
            PlaceholderText = "Select a service, or add a web service to open it here.";
        }

        _store.Save(ConfiguredServices);
        _logger.LogInformation("Removed service {ServiceId}.", item.Definition.Id);
    }

    public bool MuteOthersDuringCalls { get; set; }

    public void ApplyNotificationCounts(IReadOnlyDictionary<string, int> counts, string? latestServiceId, bool isCall)
    {
        _toastCounts.Clear();
        foreach (var pair in counts)
        {
            _toastCounts[pair.Key] = pair.Value;
        }

        RefreshBadges();

        if (isCall && latestServiceId is not null)
        {
            _titles[latestServiceId] = "incoming call";
        }

        RefreshCallMute();

        if (latestServiceId is null)
        {
            return;
        }

        var target = Services.FirstOrDefault(s => s.Definition.Id.Equals(latestServiceId, StringComparison.OrdinalIgnoreCase));
        if (target is null)
        {
            return;
        }

        var inOtherCall = MuteOthersDuringCalls
            && FindCallServiceId() is { } callId
            && !callId.Equals(target.Definition.Id, StringComparison.OrdinalIgnoreCase);
        var shouldSwitch = isCall && target.Definition.AutoSwitchOnNotification && !inOtherCall;

        if (shouldSwitch)
        {
            _logger.LogInformation("Auto-switch is enabled for {ServiceId} (call).", latestServiceId);
        }
        else
        {
            _logger.LogDebug("Badge-only for {ServiceId}. Call={IsCall}. Not stealing focus.", latestServiceId, isCall);
        }
    }

    public void RefreshCallMute()
    {
        var callId = FindCallServiceId();
        if (MuteOthersDuringCalls && callId is not null)
        {
            _webViewHost.MuteOthers(callId, true);
        }
        else
        {
            _webViewHost.MuteOthers(null, false);
        }
    }

    public Task RestoreOnExitAsync()
    {
        return _serviceManager.RestoreAllAsync();
    }

    private void OnWebTitleChanged(string serviceId, string title)
    {
        ApplyHostedTitle(serviceId, title);
    }

    public void ApplyHostedTitle(string serviceId, string title)
    {
        _titles[serviceId] = title;
        var parsedCount = NotificationMapper.TryParseUnreadFromTitle(title);
        if (parsedCount.HasValue)
        {
            _titleCounts[serviceId] = parsedCount.Value;
        }
        else
        {
            _titleCounts.Remove(serviceId);
        }
        RefreshBadges();
        RefreshCallMute();
    }

    private void OnWebAudioChanged(string serviceId, bool playing)
    {
        _playingAudio[serviceId] = playing;
        RefreshCallMute();
    }

    private void RefreshBadges()
    {
        foreach (var service in Services)
        {
            if (!service.Definition.ShowNotificationBadge)
            {
                service.UnreadCount = 0;
                continue;
            }

            var toast = _toastCounts.GetValueOrDefault(service.Definition.Id);
            var title = _titleCounts.GetValueOrDefault(service.Definition.Id);
            service.UnreadCount = Math.Max(toast, title);
        }
    }

    private string? FindCallServiceId()
    {
        foreach (var service in Services)
        {
            var id = service.Definition.Id;
            var title = _titles.GetValueOrDefault(id) ?? string.Empty;
            var audio = _playingAudio.GetValueOrDefault(id);
            if (NotificationMapper.TitleLooksLikeCall(title)
                && (audio || title.Contains("incoming call", StringComparison.OrdinalIgnoreCase)))
            {
                return id;
            }
        }

        return null;
    }

    private async Task LoadIconAndPersistAsync(ServiceItemViewModel item)
    {
        if (await LoadIconAsync(item).ConfigureAwait(true))
        {
            _store.Save(ConfiguredServices);
        }
    }

    private async Task LoadIconsAsync()
    {
        var changed = false;
        foreach (var item in Services.ToList())
        {
            if (await LoadIconAsync(item).ConfigureAwait(true))
            {
                changed = true;
            }
        }

        if (changed)
        {
            _store.Save(ConfiguredServices);
        }
    }

    private async Task<bool> LoadIconAsync(ServiceItemViewModel item)
    {
        var previous = item.Definition.IconPath;
        var path = await _iconLoader.EnsureIconAsync(item.Definition).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        item.IconImagePath = path;
        return !string.Equals(previous, path, StringComparison.OrdinalIgnoreCase);
    }
}

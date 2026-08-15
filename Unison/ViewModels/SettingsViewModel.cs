using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Unison.Models;
using Unison.Services;

namespace Unison.ViewModels;

/// <summary>
/// Appearance and notification defaults. Bound by SettingsPage.
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly UpdateCheckService _updates;
    private DateTimeOffset? _lastUpdateCheckUtc;
    private UpdateCheckResult? _pendingUpdate;

    public SettingsViewModel(AppSettings settings, UpdateCheckService updates)
    {
        _updates = updates;
        ThemeIndex = settings.Theme switch
        {
            AppTheme.Light => 1,
            AppTheme.Dark => 2,
            _ => 0
        };
        ShowNotificationBadgeByDefault = settings.ShowNotificationBadgeByDefault;
        AutoSwitchOnCallByDefault = settings.AutoSwitchOnCallByDefault;
        MuteOthersDuringCalls = settings.MuteOthersDuringCalls;
        ServiceBarIndex = settings.ServiceBarPlacement == ServiceBarPlacement.Top ? 1 : 0;
        CheckForUpdatesOnStartup = settings.CheckForUpdatesOnStartup;
        _lastUpdateCheckUtc = settings.LastUpdateCheckUtc;
        CurrentVersionText = "Version " + updates.CurrentVersion;
        UpdateStatus = "Check GitHub Releases for a newer installer.";
    }

    public IReadOnlyList<string> ThemeOptions { get; } = ["System", "Light", "Dark"];

    public IReadOnlyList<string> ServiceBarOptions { get; } = ["Sidebar", "Top bar"];

    public string CurrentVersionText { get; }

    [ObservableProperty]
    private int _themeIndex;

    [ObservableProperty]
    private bool _showNotificationBadgeByDefault;

    [ObservableProperty]
    private bool _autoSwitchOnCallByDefault;

    [ObservableProperty]
    private bool _muteOthersDuringCalls;

    [ObservableProperty]
    private int _serviceBarIndex;

    [ObservableProperty]
    private bool _checkForUpdatesOnStartup;

    [ObservableProperty]
    private string _updateStatus = "";

    [ObservableProperty]
    private bool _installVisible;

    [ObservableProperty]
    private bool _checkBusy;

    public AppSettings ToSettings() => new()
    {
        Theme = ThemeIndex switch
        {
            1 => AppTheme.Light,
            2 => AppTheme.Dark,
            _ => AppTheme.System
        },
        ShowNotificationBadgeByDefault = ShowNotificationBadgeByDefault,
        AutoSwitchOnCallByDefault = AutoSwitchOnCallByDefault,
        MuteOthersDuringCalls = MuteOthersDuringCalls,
        ServiceBarPlacement = ServiceBarIndex == 1 ? ServiceBarPlacement.Top : ServiceBarPlacement.Sidebar,
        CheckForUpdatesOnStartup = CheckForUpdatesOnStartup,
        LastUpdateCheckUtc = _lastUpdateCheckUtc
    };

    [RelayCommand]
    private async Task CheckForUpdatesAsync()
    {
        CheckBusy = true;
        InstallVisible = false;
        UpdateStatus = "Checking GitHub…";
        try
        {
            var result = await _updates.CheckLatestAsync().ConfigureAwait(true);
            _lastUpdateCheckUtc = DateTimeOffset.UtcNow;
            _pendingUpdate = result.UpdateAvailable ? result : null;
            InstallVisible = result.UpdateAvailable;
            UpdateStatus = result.ErrorMessage
                ?? (result.UpdateAvailable
                    ? $"Version {result.LatestVersion} is available. Install replaces the app and keeps your logins."
                    : "You have the latest version.");
        }
        finally
        {
            CheckBusy = false;
        }
    }

    [RelayCommand]
    private async Task InstallUpdateAsync()
    {
        if (_pendingUpdate is null)
        {
            return;
        }

        CheckBusy = true;
        try
        {
            var progress = new Progress<string>(message => UpdateStatus = message);
            var path = await _updates.DownloadInstallerAsync(_pendingUpdate, progress).ConfigureAwait(true);
            if (string.IsNullOrWhiteSpace(path))
            {
                UpdateStatus = "Download failed.";
                return;
            }

            UpdateStatus = "Starting installer…";
            _updates.LaunchInstallerAndExit(path);
        }
        catch (Exception)
        {
            UpdateStatus = "Could not download or start the installer.";
        }
        finally
        {
            CheckBusy = false;
        }
    }
}

using CommunityToolkit.Mvvm.ComponentModel;
using Unison.Models;

namespace Unison.ViewModels;

/// <summary>
/// Appearance and notification defaults. Bound by SettingsPage.
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    public SettingsViewModel(AppSettings settings)
    {
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
    }

    public IReadOnlyList<string> ThemeOptions { get; } = ["System", "Light", "Dark"];

    public IReadOnlyList<string> ServiceBarOptions { get; } = ["Sidebar", "Top bar"];

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
        ServiceBarPlacement = ServiceBarIndex == 1 ? ServiceBarPlacement.Top : ServiceBarPlacement.Sidebar
    };
}

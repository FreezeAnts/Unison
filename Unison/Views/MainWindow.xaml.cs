using Microsoft.Extensions.Logging;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Unison.Discovery;
using Unison.Models;
using Unison.Notifications;
using Unison.Persistence;
using Unison.Services;
using Unison.Services.Web;
using Unison.ViewModels;
using Unison.Windows;
using WinRT.Interop;

namespace Unison.Views;

/// <summary>
/// Main Unison window: sidebar + content host.
/// Created by App. Measures ContentHost in screen pixels and restores native windows on close.
/// </summary>
public sealed partial class MainWindow : Window
{
    private readonly ServiceManager _serviceManager;
    private readonly NotificationManager _notificationManager;
    private readonly AppSettingsStore _settingsStore;
    private readonly UpdateCheckService _updateCheck;
    private AppSettings _settings;

    public Visibility BoolToVisibility(bool value) =>
        value ? Visibility.Visible : Visibility.Collapsed;

    public Visibility BoolToVisibility(string? path) =>
        string.IsNullOrWhiteSpace(path) ? Visibility.Collapsed : Visibility.Visible;

    public ImageSource? FileToImage(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        return new BitmapImage(new Uri(path));
    }

    public string? HeaderIconPath { get; }

    public MainViewModel ViewModel { get; }

    public MainWindow()
    {
        var icoPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Unison.ico");
        if (File.Exists(icoPath))
        {
            HeaderIconPath = icoPath;
        }

        InitializeComponent();
        ApplyCustomTitleBar();

        if (!string.IsNullOrWhiteSpace(HeaderIconPath))
        {
            AppWindow.SetIcon(HeaderIconPath);
        }

        var loggerFactory = App.LoggerFactory;
        var processLocator = new ProcessLocator(loggerFactory.CreateLogger<ProcessLocator>());
        var windowDiscovery = new WindowDiscoveryService(processLocator, loggerFactory.CreateLogger<WindowDiscoveryService>());
        var nativeWindowManager = new NativeWindowManager(loggerFactory.CreateLogger<NativeWindowManager>());
        nativeWindowManager.SetUnisonHost(WindowNative.GetWindowHandle(this));
        var webViewHost = new WebViewHost(WebHost, loggerFactory.CreateLogger<WebViewHost>());
        _serviceManager = new ServiceManager(windowDiscovery, nativeWindowManager, processLocator, webViewHost, loggerFactory);
        var store = new ServiceConfigurationStore(loggerFactory.CreateLogger<ServiceConfigurationStore>());
        _settingsStore = new AppSettingsStore(loggerFactory.CreateLogger<AppSettingsStore>());
        _settings = _settingsStore.Load();
        _updateCheck = new UpdateCheckService(loggerFactory.CreateLogger<UpdateCheckService>());
        var iconLoader = new IconLoader(loggerFactory.CreateLogger<IconLoader>());

        ViewModel = new MainViewModel(store, _serviceManager, iconLoader, webViewHost, loggerFactory.CreateLogger<MainViewModel>());
        ViewModel.MuteOthersDuringCalls = _settings.MuteOthersDuringCalls;
        ViewModel.ActivateRequested += () =>
        {
            AppWindow.Show();
            var hwnd = WindowNative.GetWindowHandle(this);
            Win32.SetForegroundWindow(hwnd);
        };
        _serviceManager.HostedWindowTitleChanged += (id, title) =>
        {
            Content.DispatcherQueue.TryEnqueue(() => ViewModel.ApplyHostedTitle(id, title));
        };
        _notificationManager = new NotificationManager(loggerFactory.CreateLogger<NotificationManager>());
        Closed += MainWindow_Closed;
        ContentHost.SizeChanged += ContentHost_SizeChanged;
        AppWindow.Changed += AppWindow_Changed;
        Activated += MainWindow_Activated;
        ApplyTheme(_settings.Theme);
        ApplyServiceBarLayout();
        if (Content is FrameworkElement root)
        {
            root.ActualThemeChanged += (_, _) => RecolorTitleBarButtons(root.ActualTheme);
        }
    }

    private void ApplyCustomTitleBar()
    {
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        if (!AppWindowTitleBar.IsCustomizationSupported())
        {
            return;
        }

        var titleBar = AppWindow.TitleBar;
        titleBar.ExtendsContentIntoTitleBar = true;
        titleBar.ButtonBackgroundColor = Colors.Transparent;
        titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        RecolorTitleBarButtons(ElementTheme.Default);
    }

    private void ApplyTheme(AppTheme theme)
    {
        if (Content is not FrameworkElement root)
        {
            return;
        }

        root.RequestedTheme = theme switch
        {
            AppTheme.Light => ElementTheme.Light,
            AppTheme.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default
        };
        RecolorTitleBarButtons(root.ActualTheme);
    }

    private void RecolorTitleBarButtons(ElementTheme actualTheme)
    {
        if (!AppWindowTitleBar.IsCustomizationSupported())
        {
            return;
        }

        var dark = actualTheme == ElementTheme.Dark
            || (actualTheme == ElementTheme.Default
                && Application.Current.RequestedTheme == ApplicationTheme.Dark);
        var titleBar = AppWindow.TitleBar;
        if (dark)
        {
            titleBar.ButtonHoverBackgroundColor = global::Windows.UI.Color.FromArgb(40, 255, 255, 255);
            titleBar.ButtonPressedBackgroundColor = global::Windows.UI.Color.FromArgb(70, 255, 255, 255);
            titleBar.ButtonForegroundColor = Colors.White;
            titleBar.ButtonInactiveForegroundColor = global::Windows.UI.Color.FromArgb(160, 255, 255, 255);
        }
        else
        {
            titleBar.ButtonHoverBackgroundColor = global::Windows.UI.Color.FromArgb(40, 0, 0, 0);
            titleBar.ButtonPressedBackgroundColor = global::Windows.UI.Color.FromArgb(70, 0, 0, 0);
            titleBar.ButtonForegroundColor = Colors.Black;
            titleBar.ButtonInactiveForegroundColor = global::Windows.UI.Color.FromArgb(160, 0, 0, 0);
        }
    }

    private void ApplyServiceBarLayout()
    {
        var top = _settings.ServiceBarPlacement == ServiceBarPlacement.Top;
        var resources = ((FrameworkElement)Content).Resources;

        WorkspaceGrid.ColumnDefinitions.Clear();
        WorkspaceGrid.RowDefinitions.Clear();
        ServiceChrome.ColumnDefinitions.Clear();
        ServiceChrome.RowDefinitions.Clear();

        if (top)
        {
            WorkspaceGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            WorkspaceGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            WorkspaceGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetRow(ServiceChrome, 0);
            Grid.SetColumn(ServiceChrome, 0);
            Grid.SetRowSpan(ServiceChrome, 1);
            Grid.SetColumnSpan(ServiceChrome, 1);
            Grid.SetRow(ContentHost, 1);
            Grid.SetColumn(ContentHost, 0);
            Grid.SetRowSpan(ContentHost, 1);
            Grid.SetColumnSpan(ContentHost, 1);

            ServiceChrome.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            ServiceChrome.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            ServiceChrome.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(ServiceList, 0);
            Grid.SetColumn(ServiceList, 0);
            Grid.SetRowSpan(ServiceList, 1);
            Grid.SetColumnSpan(ServiceList, 1);
            Grid.SetRow(ServiceChromeButtons, 0);
            Grid.SetColumn(ServiceChromeButtons, 1);
            Grid.SetRowSpan(ServiceChromeButtons, 1);
            Grid.SetColumnSpan(ServiceChromeButtons, 1);

            ServiceChromeButtons.Orientation = Orientation.Horizontal;
            ServiceChromeButtons.VerticalAlignment = VerticalAlignment.Center;
            AddServiceButton.HorizontalAlignment = HorizontalAlignment.Left;
            SettingsButton.HorizontalAlignment = HorizontalAlignment.Left;
            AddServiceButton.Margin = new Thickness(4, 8, 4, 8);
            SettingsButton.Margin = new Thickness(4, 8, 12, 8);
            ProductLabel.Visibility = Visibility.Collapsed;
            ServiceList.ItemTemplate = (DataTemplate)resources["TopServiceTemplate"];
            ServiceList.ItemsPanel = (ItemsPanelTemplate)resources["HorizontalServicePanel"];
            ServiceList.Height = 56;
            ScrollViewer.SetHorizontalScrollMode(ServiceList, ScrollMode.Enabled);
            ScrollViewer.SetHorizontalScrollBarVisibility(ServiceList, ScrollBarVisibility.Auto);
            ScrollViewer.SetVerticalScrollMode(ServiceList, ScrollMode.Disabled);
            ScrollViewer.SetVerticalScrollBarVisibility(ServiceList, ScrollBarVisibility.Hidden);
        }
        else
        {
            WorkspaceGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
            WorkspaceGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            WorkspaceGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            Grid.SetRow(ServiceChrome, 0);
            Grid.SetColumn(ServiceChrome, 0);
            Grid.SetRowSpan(ServiceChrome, 1);
            Grid.SetColumnSpan(ServiceChrome, 1);
            Grid.SetRow(ContentHost, 0);
            Grid.SetColumn(ContentHost, 1);
            Grid.SetRowSpan(ContentHost, 1);
            Grid.SetColumnSpan(ContentHost, 1);

            ServiceChrome.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            ServiceChrome.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(ServiceList, 0);
            Grid.SetColumn(ServiceList, 0);
            Grid.SetRow(ServiceChromeButtons, 1);
            Grid.SetColumn(ServiceChromeButtons, 0);

            ServiceChromeButtons.Orientation = Orientation.Vertical;
            ServiceChromeButtons.VerticalAlignment = VerticalAlignment.Stretch;
            AddServiceButton.HorizontalAlignment = HorizontalAlignment.Stretch;
            SettingsButton.HorizontalAlignment = HorizontalAlignment.Stretch;
            AddServiceButton.Margin = new Thickness(12, 8, 12, 4);
            SettingsButton.Margin = new Thickness(12, 0, 12, 8);
            ProductLabel.Visibility = Visibility.Visible;
            ServiceList.ItemTemplate = (DataTemplate)resources["SidebarServiceTemplate"];
            ServiceList.ItemsPanel = (ItemsPanelTemplate)resources["VerticalServicePanel"];
            ServiceList.Height = double.NaN;
            ScrollViewer.SetHorizontalScrollMode(ServiceList, ScrollMode.Disabled);
            ScrollViewer.SetHorizontalScrollBarVisibility(ServiceList, ScrollBarVisibility.Disabled);
            ScrollViewer.SetVerticalScrollMode(ServiceList, ScrollMode.Enabled);
            ScrollViewer.SetVerticalScrollBarVisibility(ServiceList, ScrollBarVisibility.Auto);
        }

        _ = PushHostBoundsAsync();
    }

    private bool _notificationsStarted;

    private async void MainWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        if (_notificationsStarted || args.WindowActivationState == WindowActivationState.Deactivated)
        {
            return;
        }

        _notificationsStarted = true;
        var dispatcher = Content.DispatcherQueue;
        await _notificationManager.StartAsync(
            () => ViewModel.ConfiguredServices,
            (counts, latestId, isCall) =>
            {
                dispatcher.TryEnqueue(() => ViewModel.ApplyNotificationCounts(counts, latestId, isCall));
            });
        ViewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(ViewModel.SelectedService) && ViewModel.SelectedService is { } selected)
            {
                _notificationManager.Acknowledge(selected.Definition.Id);
            }
        };
        await TryStartupUpdateCheckAsync();
    }

    private async void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        _notificationManager.Stop();
        await ViewModel.RestoreOnExitAsync().ConfigureAwait(true);
    }

    private async void ContentHost_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        await PushHostBoundsAsync();
    }

    private async void AppWindow_Changed(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (args.DidPositionChange || args.DidSizeChange)
        {
            await PushHostBoundsAsync();
        }
    }

    private async Task PushHostBoundsAsync()
    {
        if (ContentHost.XamlRoot is null)
        {
            return;
        }

        var hwnd = WindowNative.GetWindowHandle(this);
        var origin = new Win32.POINT { X = 0, Y = 0 };
        Win32.ClientToScreen(hwnd, ref origin);

        var transform = ContentHost.TransformToVisual((UIElement)Content);
        var topLeft = transform.TransformPoint(new global::Windows.Foundation.Point(0, 0));
        var scale = ContentHost.XamlRoot.RasterizationScale;
        var chrome = ServiceChrome.TransformToVisual((UIElement)Content).TransformPoint(new global::Windows.Foundation.Point(0, 0));

        var bounds = new HostRect(
            origin.X + (int)Math.Round(topLeft.X * scale),
            origin.Y + (int)Math.Round(topLeft.Y * scale),
            (int)Math.Round(ContentHost.ActualWidth * scale),
            (int)Math.Round(ContentHost.ActualHeight * scale));

        var chromeLeft = origin.X + (int)Math.Round(chrome.X * scale);
        var chromeTop = origin.Y + (int)Math.Round(chrome.Y * scale);
        var chromeRight = chromeLeft + (int)Math.Round(ServiceChrome.ActualWidth * scale);
        var chromeBottom = chromeTop + (int)Math.Round(ServiceChrome.ActualHeight * scale);
        var topBar = _settings.ServiceBarPlacement == ServiceBarPlacement.Top;
        if (topBar && bounds.Top < chromeBottom)
        {
            var delta = chromeBottom - bounds.Top;
            bounds = bounds with { Top = chromeBottom, Height = Math.Max(0, bounds.Height - delta) };
        }
        else if (!topBar && bounds.Left < chromeRight)
        {
            var delta = chromeRight - bounds.Left;
            bounds = bounds with { Left = chromeRight, Width = Math.Max(0, bounds.Width - delta) };
        }

        await _serviceManager.UpdateHostBoundsAsync(bounds).ConfigureAwait(true);
    }

    private async Task TryStartupUpdateCheckAsync()
    {
        if (!_settings.CheckForUpdatesOnStartup)
        {
            return;
        }

        if (!_updateCheck.ShouldCheckOnStartup(_settings.LastUpdateCheckUtc, DateTimeOffset.UtcNow))
        {
            return;
        }

        var result = await _updateCheck.CheckLatestAsync().ConfigureAwait(true);
        _settings.LastUpdateCheckUtc = DateTimeOffset.UtcNow;
        _settingsStore.Save(_settings);

        if (!string.IsNullOrWhiteSpace(result.ErrorMessage) || !result.UpdateAvailable)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            Title = "Update available",
            Content = $"Unison {result.LatestVersion} is available (you have {result.CurrentVersion}). Install now? Your services and logins stay on this PC.",
            PrimaryButtonText = "Install",
            CloseButtonText = "Later",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        try
        {
            var path = await _updateCheck.DownloadInstallerAsync(result, null).ConfigureAwait(true);
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            _updateCheck.LaunchInstallerAndExit(path);
        }
        catch (Exception)
        {
            var failed = new ContentDialog
            {
                Title = "Update failed",
                Content = "Could not download or start the installer. Try Settings → Check for updates.",
                CloseButtonText = "OK",
                XamlRoot = Content.XamlRoot
            };
            await failed.ShowAsync();
        }
    }

    private async void AddServiceButton_Click(object sender, RoutedEventArgs e)
    {
        var scanner = new InstalledApplicationScanner(App.LoggerFactory.CreateLogger<InstalledApplicationScanner>());
        var catalog = new WebServiceCatalog();
        var guesser = new WebsiteGuessService(App.LoggerFactory.CreateLogger<WebsiteGuessService>());
        var addViewModel = new AddServiceViewModel(scanner, catalog, guesser, ViewModel.ConfiguredServices, _settings);
        var page = new AddServicePage(addViewModel);
        var dialog = new ContentDialog
        {
            Title = "Add Service",
            Content = page,
            CloseButtonText = "Close",
            XamlRoot = Content.XamlRoot
        };

        addViewModel.ServiceChosen += async (_, definition) =>
        {
            ViewModel.TryAddService(definition);
            dialog.Hide();
            await Task.CompletedTask;
        };

        await dialog.ShowAsync();
    }

    private async void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var settingsViewModel = new SettingsViewModel(_settings, _updateCheck);
        var page = new SettingsPage(settingsViewModel);
        var dialog = new ContentDialog
        {
            Title = "Settings",
            Content = page,
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot
        };

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary)
        {
            return;
        }

        _settings = settingsViewModel.ToSettings();
        _settingsStore.Save(_settings);
        ViewModel.MuteOthersDuringCalls = _settings.MuteOthersDuringCalls;
        ViewModel.RefreshCallMute();
        ApplyTheme(_settings.Theme);
        ApplyServiceBarLayout();
    }

    private async void RemoveServiceMenu_Click(object sender, RoutedEventArgs e)
    {
        var item = (sender as FrameworkElement)?.DataContext as ServiceItemViewModel
            ?? ((sender as FrameworkElement)?.Parent as FrameworkElement)?.DataContext as ServiceItemViewModel;
        if (item is null && sender is MenuFlyoutItem flyoutItem)
        {
            item = flyoutItem.DataContext as ServiceItemViewModel;
        }

        if (item is null)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            Title = "Remove service",
            Content = $"Remove {item.Name} from Unison?",
            PrimaryButtonText = "Remove",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Content.XamlRoot
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        await ViewModel.RemoveServiceAsync(item);
        await PushHostBoundsAsync();
    }

    private void ServiceList_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
    {
        ViewModel.PersistOrder();
    }

    private async void ServiceList_ItemClick(object sender, ItemClickEventArgs e)
    {
        var item = e.ClickedItem as ServiceItemViewModel;
        var contains = item is not null && ViewModel.Services.Contains(item);
        if (item is not null && contains)
        {
            await ViewModel.SelectServiceCommand.ExecuteAsync(item);
            await PushHostBoundsAsync();
        }
    }
}

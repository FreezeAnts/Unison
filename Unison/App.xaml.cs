using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Unison.Views;

namespace Unison;

/// <summary>
/// Application entry point. Creates logging and shows the main window.
/// Called by the WinUI host on launch. Creates MainWindow.
/// </summary>
public partial class App : Application
{
    private Window? _window;

    public static ILoggerFactory LoggerFactory { get; } = Microsoft.Extensions.Logging.LoggerFactory.Create(builder =>
    {
        builder
            .SetMinimumLevel(LogLevel.Debug)
            .AddDebug();
    });

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Activate();
    }
}

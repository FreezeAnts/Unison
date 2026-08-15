using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Unison.Models;
using Unison.Windows;

namespace Unison.Services.Native;

/// <summary>
/// Shared native-app adapter. Starts a process, finds its main window, and asks NativeWindowManager to place it.
/// Subclasses override RankMainWindow. Called by ServiceManager.
/// </summary>
public abstract class NativeApplicationAdapter : IServiceAdapter
{
    private static readonly TimeSpan MainWindowTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

    protected NativeApplicationAdapter(
        ServiceDefinition definition,
        WindowDiscoveryService windowDiscovery,
        NativeWindowManager nativeWindowManager,
        ProcessLocator processLocator,
        ILogger logger)
    {
        Definition = definition;
        WindowDiscovery = windowDiscovery;
        NativeWindowManager = nativeWindowManager;
        ProcessLocator = processLocator;
        Logger = logger;
    }

    public ServiceDefinition Definition { get; }

    protected WindowDiscoveryService WindowDiscovery { get; }

    protected NativeWindowManager NativeWindowManager { get; }

    protected ProcessLocator ProcessLocator { get; }

    protected ILogger Logger { get; }

    protected IntPtr? ManagedWindow { get; set; }

    protected HostRect HostBounds { get; private set; }

    public virtual async Task StartAsync(CancellationToken cancellationToken = default)
    {
        var names = GetProcessNames();
        if (names.Count == 0)
        {
            Logger.LogWarning("Service {ServiceId} has no ProcessName.", Definition.Id);
            return;
        }

        var running = ProcessLocator.FindByNames(names);
        if (running.Count > 0)
        {
            Logger.LogInformation("{ServiceId} is already running ({Count} process(es)).", Definition.Id, running.Count);
            return;
        }

        TryLaunch();
        await Task.CompletedTask.ConfigureAwait(false);
    }

    public virtual async Task<IntPtr?> FindMainWindowAsync(CancellationToken cancellationToken = default)
    {
        var names = GetProcessNames();
        if (names.Count == 0)
        {
            return null;
        }

        var deadline = DateTime.UtcNow + MainWindowTimeout;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var windows = names
                .SelectMany(WindowDiscovery.FindWindowsByProcessName)
                .DistinctBy(w => w.Handle)
                .ToList();
            var chosen = RankMainWindow(windows);
            if (chosen is not null)
            {
                Logger.LogInformation(
                    "Chose main window {Handle} for {ServiceId}: title='{Title}' class='{Class}' area={Area}.",
                    chosen.Handle,
                    Definition.Id,
                    chosen.Title,
                    chosen.ClassName,
                    chosen.Area);
                return chosen.Handle;
            }

            Logger.LogDebug("No main window yet for {ServiceId}; {Count} candidate(s).", Definition.Id, windows.Count);
            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(true);
        }

        Logger.LogWarning("Timed out waiting for a main window for {ServiceId}. If the app is elevated, Unison may not see it.", Definition.Id);
        return null;
    }

    public virtual async Task ActivateAsync(CancellationToken cancellationToken = default)
    {
        var handle = ManagedWindow is { } existing && Win32.IsWindow(existing)
            ? existing
            : await FindMainWindowAsync(cancellationToken).ConfigureAwait(true);

        if (handle is null || handle == IntPtr.Zero)
        {
            Logger.LogWarning("Cannot activate {ServiceId}; no main window.", Definition.Id);
            return;
        }

        ManagedWindow = handle;
        NativeWindowManager.Remember(handle.Value);
        if (HostBounds.IsValid)
        {
            NativeWindowManager.FitToRect(handle.Value, HostBounds);
        }

        NativeWindowManager.Show(handle.Value);
    }

    public virtual Task DeactivateAsync(CancellationToken cancellationToken = default)
    {
        if (ManagedWindow is { } handle)
        {
            NativeWindowManager.Hide(handle);
        }

        return Task.CompletedTask;
    }

    public virtual Task RestoreAsync(CancellationToken cancellationToken = default)
    {
        if (ManagedWindow is { } handle)
        {
            NativeWindowManager.Restore(handle);
            ManagedWindow = null;
        }

        return Task.CompletedTask;
    }

    public virtual Task ApplyHostBoundsAsync(HostRect bounds, CancellationToken cancellationToken = default)
    {
        HostBounds = bounds;
        if (ManagedWindow is { } handle && Win32.IsWindow(handle) && bounds.IsValid)
        {
            NativeWindowManager.FitToRect(handle, bounds);
        }

        return Task.CompletedTask;
    }

    protected virtual IReadOnlyList<string> GetProcessNames()
    {
        return string.IsNullOrWhiteSpace(Definition.ProcessName)
            ? []
            : Definition.ProcessName
                .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
    }

    protected virtual void TryLaunch()
    {
        var fileName = Definition.ExecutablePath ?? GetProcessNames().FirstOrDefault();
        if (string.IsNullOrWhiteSpace(fileName))
        {
            Logger.LogWarning("Cannot launch {ServiceId}; no executable or process name.", Definition.Id);
            return;
        }

        if (!fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            && fileName.IndexOf('\\') < 0
            && fileName.IndexOf(':') < 0)
        {
            fileName += ".exe";
        }

        try
        {
            Logger.LogInformation("Launching {FileName} for {ServiceId}.", fileName, Definition.Id);
            if (fileName.StartsWith("shell:", StringComparison.OrdinalIgnoreCase))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = fileName,
                    UseShellExecute = true
                });
                return;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = fileName,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to launch {FileName} for {ServiceId}. The app may be missing or running elevated.", fileName, Definition.Id);
        }
    }

    protected virtual DiscoveredWindow? RankMainWindow(IReadOnlyList<DiscoveredWindow> windows)
    {
        return windows
            .Where(IsPlausibleMainWindow)
            .OrderByDescending(w => w.Area)
            .FirstOrDefault();
    }

    protected static bool IsPlausibleMainWindow(DiscoveredWindow window)
    {
        return window.IsVisible
            && !window.IsToolWindow
            && window.OwnerHandle == IntPtr.Zero
            && !string.IsNullOrWhiteSpace(window.Title);
    }
}

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

            var windows = CollectCandidateWindows(names);
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

    public string ReadWindowTitle()
    {
        if (ManagedWindow is not { } handle || !Win32.IsWindow(handle))
        {
            return string.Empty;
        }

        var length = Win32.GetWindowTextLength(handle);
        if (length <= 0)
        {
            return string.Empty;
        }

        var builder = new System.Text.StringBuilder(length + 1);
        _ = Win32.GetWindowText(handle, builder, builder.Capacity);
        return builder.ToString();
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
        var names = string.IsNullOrWhiteSpace(Definition.ProcessName)
            ? new List<string>()
            : Definition.ProcessName
                .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();

        var display = Definition.Name ?? string.Empty;
        if (display.Contains("Teams", StringComparison.OrdinalIgnoreCase)
            || names.Any(n => n.Contains("Teams", StringComparison.OrdinalIgnoreCase)))
        {
            names.Add("ms-teams");
            names.Add("Teams");
        }

        return names.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
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

    private List<DiscoveredWindow> CollectCandidateWindows(IReadOnlyList<string> names)
    {
        var windows = names
            .SelectMany(WindowDiscovery.FindWindowsByProcessName)
            .ToList();

        var shellLaunch = Definition.ExecutablePath?.StartsWith("shell:", StringComparison.OrdinalIgnoreCase) == true;
        if (shellLaunch)
        {
            windows.AddRange(WindowDiscovery.FindWindowsByProcessName("ApplicationFrameHost"));
            windows.AddRange(WindowDiscovery.FindWindowsByProcessName("msedge"));
            windows.AddRange(WindowDiscovery.FindWindowsByProcessName("msedgewebview2"));
        }

        return windows.DistinctBy(w => w.Handle).ToList();
    }

    protected bool IsPlausibleMainWindow(DiscoveredWindow window)
    {
        var name = Definition.Name;
        var title = window.Title ?? string.Empty;
        var chrome = window.ClassName.Contains("Chrome_WidgetWin", StringComparison.OrdinalIgnoreCase);
        var frame = window.ClassName.Equals("ApplicationFrameWindow", StringComparison.OrdinalIgnoreCase)
            || window.ClassName.Equals("WinUIDesktopWin32WindowClass", StringComparison.OrdinalIgnoreCase);
        var titleMatches = !string.IsNullOrWhiteSpace(title)
            && (title.Contains(name, StringComparison.OrdinalIgnoreCase)
                || title.Contains("Discord", StringComparison.OrdinalIgnoreCase)
                    && name.Contains("Discord", StringComparison.OrdinalIgnoreCase));

        if ((chrome || frame) && titleMatches && window.Area >= 8_000)
        {
            return true;
        }

        if (window.Area < 8_000)
        {
            return false;
        }

        return !window.IsToolWindow
            && window.OwnerHandle == IntPtr.Zero
            && !string.IsNullOrWhiteSpace(title);
    }
}

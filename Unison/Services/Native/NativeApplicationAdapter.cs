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

    private static readonly HashSet<string> HostProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "ApplicationFrameHost",
        "msedge",
        "msedgewebview2"
    };

    private static readonly HashSet<string> WeakNameTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "app", "application", "desktop", "inc", "llc", "microsoft", "store", "the", "windows"
    };

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

    protected int? LaunchProcessId { get; private set; }

    protected string? PreferredWindowClass { get; private set; }

    public virtual async Task StartAsync(CancellationToken cancellationToken = default)
    {
        var names = GetProcessNames();
        if (names.Count == 0 && string.IsNullOrWhiteSpace(Definition.ExecutablePath))
        {
            Logger.LogWarning("Service {ServiceId} has no ProcessName or ExecutablePath.", Definition.Id);
            return;
        }

        if (IsAlreadyRunning(names))
        {
            Logger.LogInformation(
                "Service {ServiceId} already appears to be running; skipping launch.",
                Definition.Id);
            return;
        }

        TryLaunch();
        await Task.CompletedTask.ConfigureAwait(false);
    }

    public virtual async Task<IntPtr?> FindMainWindowAsync(CancellationToken cancellationToken = default)
    {
        var names = GetProcessNames();
        if (names.Count == 0 && LaunchProcessId is null && !UsesHostProcessDiscovery())
        {
            return null;
        }

        var deadline = DateTime.UtcNow + MainWindowTimeout;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Prefer the app's own process (Dialpad.exe, ms-teams.exe, ...). Only fall
            // back to host processes (Edge / ApplicationFrameHost) when the app has no
            // real window of its own, so we never grab a random browser/frame window.
            var windows = CollectCandidateWindows(names, includeHostProcesses: false);
            var chosen = RankMainWindow(windows);
            if (chosen is null && UsesHostProcessDiscovery())
            {
                windows = CollectCandidateWindows(names, includeHostProcesses: true);
                chosen = RankMainWindow(windows);
            }

            if (chosen is not null)
            {
                PreferredWindowClass = chosen.ClassName;
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

    /// <summary>True once a live window has been captured for this service.</summary>
    public bool HasManagedWindow => ManagedWindow is { } handle && Win32.IsWindow(handle);

    public virtual async Task ActivateAsync(CancellationToken cancellationToken = default)
    {
        var handle = ManagedWindow is { } existing && Win32.IsWindow(existing)
            ? existing
            : await FindMainWindowAsync(cancellationToken).ConfigureAwait(true);

        if (handle is null || handle == IntPtr.Zero)
        {
            Logger.LogWarning("Cannot activate {ServiceId}; no main window.", Definition.Id);
            // Throw so the caller can show a failure placeholder instead of a blank pane
            // while the real app sits in its own window on the desktop.
            throw new InvalidOperationException(
                $"Could not capture the {Definition.Name} window. If it opened in its own window, click it in the taskbar once, then select it here again.");
        }

        ManagedWindow = handle;
        NativeWindowManager.Remember(handle.Value);
        NativeWindowManager.Show(handle.Value);
        if (HostBounds.IsValid)
        {
            NativeWindowManager.FitToRect(handle.Value, HostBounds);
        }
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
            // Park off-screen, never SW_HIDE: hidden Chromium/Electron windows
            // (Teams, Dialpad) come back blank, exactly like classic Outlook.
            NativeWindowManager.Conceal(handle);
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

        PreferredWindowClass = null;
        LaunchProcessId = null;
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
        LaunchProcessId = null;
        var fileName = Definition.ExecutablePath ?? GetProcessNames().FirstOrDefault(n => !IsHostProcessName(n));
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

            var started = Process.Start(new ProcessStartInfo
            {
                FileName = fileName,
                UseShellExecute = true
            });
            if (started is not null)
            {
                try
                {
                    LaunchProcessId = started.Id;
                }
                catch (InvalidOperationException)
                {
                    LaunchProcessId = null;
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to launch {FileName} for {ServiceId}. The app may be missing or running elevated.", fileName, Definition.Id);
        }
    }

    protected virtual DiscoveredWindow? RankMainWindow(IReadOnlyList<DiscoveredWindow> windows)
    {
        var scored = windows
            .Select(w => (Window: w, Score: ScoreMainWindow(w)))
            .Where(x => x.Score > 0 && IsPlausibleMainWindow(x.Window))
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.Window.Area)
            .ToList();

        if (scored.Count == 0)
        {
            return null;
        }

        var titled = scored.Where(x => TitleMatchesService(x.Window.Title)).ToList();
        if (titled.Count > 0)
        {
            return titled[0].Window;
        }

        // Never pick a random host (Edge/AFH) window just because it is large.
        if (scored.All(x => IsHostProcessName(x.Window.ProcessName)))
        {
            return null;
        }

        return scored.First(x => !IsHostProcessName(x.Window.ProcessName)).Window;
    }

    protected virtual int ScoreMainWindow(DiscoveredWindow window)
    {
        var score = window.Area / 10_000;
        var titleMatches = TitleMatchesService(window.Title);
        var chrome = window.ClassName.Contains("Chrome_WidgetWin", StringComparison.OrdinalIgnoreCase);
        var frame = window.ClassName.Equals("ApplicationFrameWindow", StringComparison.OrdinalIgnoreCase)
            || window.ClassName.Equals("WinUIDesktopWin32WindowClass", StringComparison.OrdinalIgnoreCase);

        if (titleMatches)
        {
            score += 80;
        }

        if (window.IsVisible)
        {
            score += 20;
        }
        else
        {
            score -= 30;
        }

        if (PreferredWindowClass is not null
            && window.ClassName.Equals(PreferredWindowClass, StringComparison.OrdinalIgnoreCase))
        {
            score += 40;
        }

        if ((chrome || frame) && titleMatches)
        {
            score += 40;
        }

        if (LaunchProcessId is { } pid && window.ProcessId == pid)
        {
            score += 50;
        }

        if (LooksLikeGenericSplash(window))
        {
            score -= 100;
        }

        if (window.IsToolWindow)
        {
            score -= 50;
        }

        if (window.OwnerHandle != IntPtr.Zero)
        {
            score -= 40;
        }

        if (string.IsNullOrWhiteSpace(window.Title) || window.Title.Length < 2)
        {
            score -= 60;
        }

        if (window.Area < 8_000)
        {
            score -= 100;
        }

        // Host windows without a title match are never viable.
        if (IsHostProcessName(window.ProcessName) && !titleMatches)
        {
            return int.MinValue / 4;
        }

        return score;
    }

    protected bool IsPlausibleMainWindow(DiscoveredWindow window)
    {
        if (window.IsCloaked || window.IsToolWindow)
        {
            return false;
        }

        // Minimised main windows report a tiny off-screen rect; they are still
        // capturable because FitToRect restores them before sizing.
        if (!window.IsMinimized && window.Area < 8_000)
        {
            return false;
        }

        if (window.OwnerHandle != IntPtr.Zero)
        {
            return false;
        }

        if (LooksLikeGenericSplash(window) || LooksLikeAuxiliaryWindow(window.Title))
        {
            return false;
        }

        var title = window.Title ?? string.Empty;
        if (string.IsNullOrWhiteSpace(title))
        {
            return false;
        }

        if (IsHostProcessName(window.ProcessName))
        {
            return TitleMatchesService(title);
        }

        var chrome = window.ClassName.Contains("Chrome_WidgetWin", StringComparison.OrdinalIgnoreCase);
        var frame = window.ClassName.Equals("ApplicationFrameWindow", StringComparison.OrdinalIgnoreCase)
            || window.ClassName.Equals("WinUIDesktopWin32WindowClass", StringComparison.OrdinalIgnoreCase);
        if ((chrome || frame) && TitleMatchesService(title))
        {
            return true;
        }

        return true;
    }

    protected bool TitleMatchesService(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return false;
        }

        foreach (var token in GetNameTokens())
        {
            if (title.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    protected IReadOnlyList<string> GetNameTokens()
    {
        var tokens = new List<string>();
        var name = Definition.Name?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(name))
        {
            tokens.Add(name);
            foreach (var part in name.Split([' ', '-', '_'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (part.Length >= 3 && !WeakNameTokens.Contains(part))
                {
                    tokens.Add(part);
                }
            }
        }

        foreach (var processName in GetProcessNames().Where(n => !IsHostProcessName(n)))
        {
            if (processName.Length >= 3)
            {
                tokens.Add(processName);
            }
        }

        return tokens.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    protected static bool IsHostProcessName(string? processName) =>
        !string.IsNullOrWhiteSpace(processName) && HostProcessNames.Contains(processName);

    protected static bool LooksLikeGenericSplash(DiscoveredWindow window)
    {
        var className = window.ClassName ?? string.Empty;
        var title = window.Title ?? string.Empty;
        return className.Equals("MsoSplash", StringComparison.OrdinalIgnoreCase)
            || className.Equals("NUIDialog", StringComparison.OrdinalIgnoreCase)
            || title.StartsWith("Opening", StringComparison.OrdinalIgnoreCase)
            || title.Equals("Loading", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Call/notification/meeting pop-outs are never the window to host.</summary>
    protected static bool LooksLikeAuxiliaryWindow(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return false;
        }

        return title.Contains("Notification", StringComparison.OrdinalIgnoreCase)
            || title.Contains("Incoming call", StringComparison.OrdinalIgnoreCase)
            || title.Contains("Call with", StringComparison.OrdinalIgnoreCase)
            || title.Contains("Meeting compact", StringComparison.OrdinalIgnoreCase)
            || title.Contains("Screen sharing", StringComparison.OrdinalIgnoreCase);
    }

    private bool IsAlreadyRunning(IReadOnlyList<string> names)
    {
        var appSpecific = names.Where(n => !IsHostProcessName(n)).ToList();
        if (appSpecific.Count > 0)
        {
            var running = ProcessLocator.FindByNames(appSpecific);
            var any = running.Count > 0;
            foreach (var process in running)
            {
                process.Dispose();
            }

            if (any)
            {
                return true;
            }
        }

        var windows = CollectCandidateWindows(names, includeHostProcesses: UsesHostProcessDiscovery());
        return RankMainWindow(windows) is not null;
    }

    private bool UsesHostProcessDiscovery()
    {
        if (Definition.ExecutablePath?.StartsWith("shell:", StringComparison.OrdinalIgnoreCase) == true)
        {
            return true;
        }

        return GetProcessNames().Any(IsHostProcessName);
    }

    private List<DiscoveredWindow> CollectCandidateWindows(IReadOnlyList<string> names, bool includeHostProcesses)
    {
        var searchNames = names
            .Where(n => includeHostProcesses || !IsHostProcessName(n))
            .ToList();
        if (includeHostProcesses)
        {
            searchNames.Add("ApplicationFrameHost");
            searchNames.Add("msedge");
            searchNames.Add("msedgewebview2");
        }

        var windows = searchNames
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .SelectMany(WindowDiscovery.FindWindowsByProcessName)
            .ToList();

        if (LaunchProcessId is { } pid)
        {
            windows.AddRange(WindowDiscovery.FindWindowsForProcess(pid));
        }

        return windows.DistinctBy(w => w.Handle).ToList();
    }
}

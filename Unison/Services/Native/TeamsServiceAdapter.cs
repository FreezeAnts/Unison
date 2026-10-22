using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Unison.Models;
using Unison.Windows;

namespace Unison.Services.Native;

/// <summary>
/// Teams-specific adapter. Hosts the main Teams window only (new or classic), not calls or notifications.
/// Created by ServiceManager. Reuses NativeApplicationAdapter for launch, place, hide, and restore.
/// </summary>
public sealed class TeamsServiceAdapter : NativeApplicationAdapter
{
    public TeamsServiceAdapter(
        ServiceDefinition definition,
        WindowDiscoveryService windowDiscovery,
        NativeWindowManager nativeWindowManager,
        ProcessLocator processLocator,
        ILogger<TeamsServiceAdapter> logger)
        : base(definition, windowDiscovery, nativeWindowManager, processLocator, logger)
    {
    }

    protected override IReadOnlyList<string> GetProcessNames() => ["ms-teams", "Teams"];

    protected override void TryLaunch()
    {
        foreach (var target in new[] { "ms-teams:", "msteams:" })
        {
            try
            {
                Logger.LogInformation("Launching Teams via {Target}.", target);
                Process.Start(new ProcessStartInfo
                {
                    FileName = target,
                    UseShellExecute = true
                });
                return;
            }
            catch (Exception ex)
            {
                Logger.LogDebug(ex, "Teams protocol {Target} failed.", target);
            }
        }

        base.TryLaunch();
    }

    protected override DiscoveredWindow? RankMainWindow(IReadOnlyList<DiscoveredWindow> windows)
    {
        // IsPlausibleMainWindow already drops cloaked, tool, owned, splash and
        // call/notification pop-out windows. A minimised main window is allowed
        // (its rect is tiny) because FitToRect restores it before sizing.
        var plausible = windows
            .Where(IsPlausibleMainWindow)
            .Where(w => LooksLikeMainTeamsWindow(w))
            .ToList();

        // Prefer the real Electron/WebView main window; fall back to any plausible
        // large window so a class-name change in a Teams update is not fatal.
        var preferred = plausible.Where(w => IsTeamsMainClass(w.ClassName)).ToList();
        var candidates = preferred.Count > 0
            ? preferred
            : plausible.Where(w => w.IsMinimized || w.Area >= 80_000).ToList();

        foreach (var candidate in candidates)
        {
            Logger.LogInformation(
                "Teams candidate {Handle}: title='{Title}' class='{Class}' area={Area} visible={Visible} minimized={Minimized} score={Score}.",
                candidate.Handle,
                candidate.Title,
                candidate.ClassName,
                candidate.Area,
                candidate.IsVisible,
                candidate.IsMinimized,
                Score(candidate));
        }

        return candidates
            .OrderByDescending(Score)
            .ThenByDescending(w => w.Area)
            .FirstOrDefault();
    }

    private static bool IsTeamsMainClass(string? className) =>
        className is not null
        && (className.Contains("Chrome_WidgetWin", StringComparison.OrdinalIgnoreCase)
            || className.Contains("TeamsWebView", StringComparison.OrdinalIgnoreCase));

    private static bool LooksLikeMainTeamsWindow(DiscoveredWindow window)
    {
        if (LooksLikeAuxiliaryWindow(window.Title))
        {
            return false;
        }

        return IsTeamsMainClass(window.ClassName)
            || window.Title.Contains("Teams", StringComparison.OrdinalIgnoreCase);
    }

    private static int Score(DiscoveredWindow window)
    {
        var score = window.Area / 10_000;
        if (window.Title.Contains("Microsoft Teams", StringComparison.OrdinalIgnoreCase)
            || window.Title.Equals("Teams", StringComparison.OrdinalIgnoreCase)
            || window.Title.Contains("Teams", StringComparison.OrdinalIgnoreCase))
        {
            score += 50;
        }

        if (IsTeamsMainClass(window.ClassName))
        {
            score += 30;
        }

        if (window.IsVisible)
        {
            score += 20;
        }
        else if (window.IsMinimized)
        {
            // Recoverable: FitToRect restores it. Still prefer a visible window.
            score += 5;
        }

        return score;
    }
}

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
        var candidates = windows.Where(IsPlausibleMainWindow).Where(w => w.Area >= 80_000).ToList();
        foreach (var candidate in candidates)
        {
            Logger.LogInformation(
                "Teams candidate {Handle}: title='{Title}' class='{Class}' area={Area} score={Score}.",
                candidate.Handle,
                candidate.Title,
                candidate.ClassName,
                candidate.Area,
                Score(candidate));
        }

        return candidates.OrderByDescending(Score).FirstOrDefault();
    }

    private static int Score(DiscoveredWindow window)
    {
        var score = window.Area / 10_000;
        if (window.Title.Contains("Microsoft Teams", StringComparison.OrdinalIgnoreCase)
            || window.Title.Equals("Teams", StringComparison.OrdinalIgnoreCase))
        {
            score += 50;
        }

        if (window.ClassName.Contains("Chrome_WidgetWin", StringComparison.OrdinalIgnoreCase)
            || window.ClassName.Contains("TeamsWebView", StringComparison.OrdinalIgnoreCase))
        {
            score += 30;
        }

        if (LooksLikeAuxiliary(window.Title))
        {
            score -= 60;
        }

        return score;
    }

    private static bool LooksLikeAuxiliary(string title)
    {
        return title.Contains("Notification", StringComparison.OrdinalIgnoreCase)
            || title.Contains("Call with", StringComparison.OrdinalIgnoreCase)
            || title.Contains("Meeting compact", StringComparison.OrdinalIgnoreCase)
            || title.Contains("Incoming call", StringComparison.OrdinalIgnoreCase);
    }
}

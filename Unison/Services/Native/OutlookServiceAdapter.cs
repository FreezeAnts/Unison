using Microsoft.Extensions.Logging;
using Unison.Models;
using Unison.Windows;

namespace Unison.Services.Native;

/// <summary>
/// Outlook-specific adapter. Manages only the classic Outlook explorer window, not inspectors or pop-outs.
/// Created by ServiceManager. Uses NativeApplicationAdapter for launch, place, hide, and restore.
/// </summary>
public sealed class OutlookServiceAdapter : NativeApplicationAdapter
{
    private const string OutlookFrameClass = "rctrl_renwnd32";

    public OutlookServiceAdapter(
        ServiceDefinition definition,
        WindowDiscoveryService windowDiscovery,
        NativeWindowManager nativeWindowManager,
        ProcessLocator processLocator,
        ILogger<OutlookServiceAdapter> logger)
        : base(definition, windowDiscovery, nativeWindowManager, processLocator, logger)
    {
    }

    protected override DiscoveredWindow? RankMainWindow(IReadOnlyList<DiscoveredWindow> windows)
    {
        var candidates = windows.Where(IsPlausibleMainWindow).ToList();
        foreach (var candidate in candidates)
        {
            Logger.LogInformation(
                "Outlook candidate {Handle}: title='{Title}' class='{Class}' area={Area} score={Score}.",
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
        if (window.ClassName.Equals(OutlookFrameClass, StringComparison.OrdinalIgnoreCase))
        {
            score += 100;
        }

        if (window.Title.Contains("Outlook", StringComparison.OrdinalIgnoreCase))
        {
            score += 50;
        }

        if (LooksLikeInspector(window.Title))
        {
            score -= 40;
        }

        return score;
    }

    private static bool LooksLikeInspector(string title)
    {
        return title.Contains("Untitled - Message", StringComparison.OrdinalIgnoreCase)
            || title.Contains("Message", StringComparison.OrdinalIgnoreCase) && !title.Contains("Outlook", StringComparison.OrdinalIgnoreCase)
            || title.Contains("Meeting", StringComparison.OrdinalIgnoreCase)
            || title.Contains("Appointment", StringComparison.OrdinalIgnoreCase);
    }
}

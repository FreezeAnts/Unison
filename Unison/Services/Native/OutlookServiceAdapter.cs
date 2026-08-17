using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Unison.Models;
using Unison.Windows;

namespace Unison.Services.Native;

/// <summary>
/// Outlook-specific adapter. Manages classic Outlook explorer and New Outlook main windows,
/// not inspectors or pop-outs. Created by ServiceManager.
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

    protected override IReadOnlyList<string> GetProcessNames() =>
        ["OUTLOOK", "olk", "HxOutlook"];

    public override async Task StartAsync(CancellationToken cancellationToken = default)
    {
        var names = GetProcessNames();
        if (names.Count == 0)
        {
            Logger.LogWarning("Service {ServiceId} has no ProcessName.", Definition.Id);
            return;
        }

        var running = ProcessLocator.FindByNames(names);
        if (running.Count == 0)
        {
            Logger.LogInformation("No Outlook process running; launching for {ServiceId}.", Definition.Id);
            TryLaunch();
        }
        else
        {
            Logger.LogInformation(
                "Outlook already running ({Count} process(es)); skipping launch for {ServiceId}.",
                running.Count,
                Definition.Id);
            foreach (var process in running)
            {
                process.Dispose();
            }
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    public override Task DeactivateAsync(CancellationToken cancellationToken = default)
    {
        if (ManagedWindow is { } handle)
        {
            NativeWindowManager.Conceal(handle);
        }

        return Task.CompletedTask;
    }

    protected override DiscoveredWindow? RankMainWindow(IReadOnlyList<DiscoveredWindow> windows)
    {
        var candidates = windows.Where(IsPlausibleMainWindow).Where(w => !LooksLikeSplashOrDialog(w)).ToList();
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

        if (window.ClassName.Equals("WinUIDesktopWin32WindowClass", StringComparison.OrdinalIgnoreCase)
            || window.ClassName.Equals("ApplicationFrameWindow", StringComparison.OrdinalIgnoreCase))
        {
            score += 80;
        }

        if (window.Title.Contains("Outlook", StringComparison.OrdinalIgnoreCase))
        {
            score += 50;
        }

        if (LooksLikeSplashOrDialog(window))
        {
            score -= 80;
        }

        if (LooksLikeInspector(window.Title))
        {
            score -= 40;
        }

        return score;
    }

    private static bool LooksLikeSplashOrDialog(DiscoveredWindow window)
    {
        var className = window.ClassName ?? string.Empty;
        var title = window.Title ?? string.Empty;
        return className.Equals("MsoSplash", StringComparison.OrdinalIgnoreCase)
            || className.Equals("NUIDialog", StringComparison.OrdinalIgnoreCase)
            || className.Equals("OpusApp", StringComparison.OrdinalIgnoreCase)
            || className.Equals("AccountSetupHiddenWindow", StringComparison.OrdinalIgnoreCase)
            || title.StartsWith("Opening", StringComparison.OrdinalIgnoreCase)
            || title.Contains("Email Account Setup", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeInspector(string title)
    {
        return title.Contains("Untitled - Message", StringComparison.OrdinalIgnoreCase)
            || title.Contains("Message", StringComparison.OrdinalIgnoreCase) && !title.Contains("Outlook", StringComparison.OrdinalIgnoreCase)
            || title.Contains("Meeting", StringComparison.OrdinalIgnoreCase)
            || title.Contains("Appointment", StringComparison.OrdinalIgnoreCase);
    }
}

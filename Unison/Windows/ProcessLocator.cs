using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Unison.Windows;

/// <summary>
/// Finds running processes by name. Called by native adapters before launching an app.
/// Logs when several processes match so we can pick a window-bearing one later.
/// </summary>
public sealed class ProcessLocator
{
    private readonly ILogger<ProcessLocator> _logger;

    public ProcessLocator(ILogger<ProcessLocator> logger)
    {
        _logger = logger;
    }

    public IReadOnlyList<Process> FindByName(string processName)
    {
        var name = processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? processName[..^4]
            : processName;

        var processes = Process.GetProcessesByName(name);
        if (processes.Length > 1)
        {
            _logger.LogWarning(
                "Found {Count} processes named {ProcessName}: {Ids}.",
                processes.Length,
                name,
                string.Join(", ", processes.Select(p => p.Id)));
        }
        else if (processes.Length == 0)
        {
            _logger.LogDebug("No running process named {ProcessName}.", name);
        }

        return processes;
    }

    public IReadOnlyList<Process> FindByNames(IEnumerable<string> processNames)
    {
        var seen = new HashSet<int>();
        var matches = new List<Process>();
        foreach (var name in processNames)
        {
            foreach (var process in FindByName(name))
            {
                if (seen.Add(process.Id))
                {
                    matches.Add(process);
                }
            }
        }

        return matches;
    }
}

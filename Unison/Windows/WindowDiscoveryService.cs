using System.Text;
using Microsoft.Extensions.Logging;

namespace Unison.Windows;

/// <summary>
/// Finds top-level HWNDs that belong to a process.
/// Called by native service adapters. Uses Win32.EnumWindows; does not use Process.MainWindowHandle.
/// </summary>
public sealed class WindowDiscoveryService
{
    private readonly ProcessLocator _processLocator;
    private readonly ILogger<WindowDiscoveryService> _logger;

    public WindowDiscoveryService(ProcessLocator processLocator, ILogger<WindowDiscoveryService> logger)
    {
        _processLocator = processLocator;
        _logger = logger;
    }

    public IReadOnlyList<DiscoveredWindow> FindWindowsByProcessName(string processName)
    {
        var processes = _processLocator.FindByName(processName);
        var matches = new List<DiscoveredWindow>();
        foreach (var process in processes)
        {
            matches.AddRange(FindWindowsForProcess(process.Id));
        }

        return matches;
    }

    public IReadOnlyList<DiscoveredWindow> FindWindowsForProcess(int processId)
    {
        var matches = new List<DiscoveredWindow>();

        Win32.EnumWindows((hWnd, _) =>
        {
            Win32.GetWindowThreadProcessId(hWnd, out var windowProcessId);
            if (windowProcessId != (uint)processId)
            {
                return true;
            }

            matches.Add(Describe(hWnd, processId));
            return true;
        }, IntPtr.Zero);

        _logger.LogDebug("Found {Count} top-level windows for process {ProcessId}.", matches.Count, processId);
        return matches;
    }

    private static DiscoveredWindow Describe(IntPtr hWnd, int processId)
    {
        var visible = Win32.IsWindowVisible(hWnd);
        var title = GetWindowTitle(hWnd);
        var className = GetClassName(hWnd);
        var owner = Win32.GetWindow(hWnd, Win32.GW_OWNER);
        var exStyle = Win32.GetWindowLongPtr(hWnd, Win32.GWL_EXSTYLE).ToInt64();
        var isToolWindow = (exStyle & Win32.WS_EX_TOOLWINDOW) != 0;
        Win32.GetWindowRect(hWnd, out var rect);
        var area = Math.Max(0, rect.Width) * Math.Max(0, rect.Height);

        return new DiscoveredWindow(hWnd, processId, title, className, owner, visible, isToolWindow, area);
    }

    private static string GetWindowTitle(IntPtr hWnd)
    {
        var length = Win32.GetWindowTextLength(hWnd);
        if (length <= 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder(length + 1);
        _ = Win32.GetWindowText(hWnd, builder, builder.Capacity);
        return builder.ToString();
    }

    private static string GetClassName(IntPtr hWnd)
    {
        var builder = new StringBuilder(256);
        _ = Win32.GetClassName(hWnd, builder, builder.Capacity);
        return builder.ToString();
    }
}

public sealed record DiscoveredWindow(
    IntPtr Handle,
    int ProcessId,
    string Title,
    string ClassName,
    IntPtr OwnerHandle,
    bool IsVisible,
    bool IsToolWindow,
    int Area);

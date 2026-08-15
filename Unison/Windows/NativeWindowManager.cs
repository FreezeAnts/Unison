using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;

namespace Unison.Windows;

/// <summary>
/// Remembers original bounds/show state of managed main windows so they can be restored on exit.
/// Called by native adapters. Uses Win32 GetWindowPlacement/SetWindowPos. Never uses SetParent.
/// Hosted windows are removed from the taskbar via ITaskbarList.DeleteTab while Unison manages them.
/// </summary>
public sealed class NativeWindowManager
{
    private static readonly int[] TaskbarHideRetryMs = [400, 1200];

    private readonly ILogger<NativeWindowManager> _logger;
    private readonly Dictionary<IntPtr, ManagedWindowState> _states = new();
    private Win32.ITaskbarList? _taskbarList;
    private bool _taskbarListFailed;

    public NativeWindowManager(ILogger<NativeWindowManager> logger)
    {
        _logger = logger;
    }

    internal IReadOnlyDictionary<IntPtr, ManagedWindowState> TrackedWindows => _states;

    public void Remember(IntPtr hWnd)
    {
        if (_states.ContainsKey(hWnd))
        {
            HideFromTaskbar(hWnd);
            return;
        }

        var placement = new Win32.WINDOWPLACEMENT { length = System.Runtime.InteropServices.Marshal.SizeOf<Win32.WINDOWPLACEMENT>() };
        if (!Win32.GetWindowPlacement(hWnd, ref placement))
        {
            Win32.GetWindowRect(hWnd, out var rect);
            placement.rcNormalPosition = rect;
            placement.showCmd = Win32.SW_SHOW;
        }

        var state = new ManagedWindowState(hWnd, placement);
        _states[hWnd] = state;
        var normal = placement.rcNormalPosition;
        _logger.LogInformation(
            "Remembered window {Handle} at {Left},{Top} {Width}x{Height} showCmd={ShowCmd}.",
            hWnd,
            normal.Left,
            normal.Top,
            normal.Width,
            normal.Height,
            placement.showCmd);
        HideFromTaskbar(hWnd);
    }

    public void FitToRect(IntPtr hWnd, HostRect bounds)
    {
        if (!Win32.IsWindow(hWnd) || !bounds.IsValid)
        {
            _logger.LogWarning("Cannot fit window {Handle}; hwnd valid={Valid}, bounds={Bounds}.", hWnd, Win32.IsWindow(hWnd), bounds);
            return;
        }

        if (Win32.IsIconic(hWnd))
        {
            Win32.ShowWindow(hWnd, Win32.SW_RESTORE);
        }

        Win32.SetWindowPos(
            hWnd,
            IntPtr.Zero,
            bounds.Left,
            bounds.Top,
            bounds.Width,
            bounds.Height,
            Win32.SWP_NOZORDER | Win32.SWP_SHOWWINDOW);
        HideFromTaskbarWithRetry(hWnd);
    }

    public void Hide(IntPtr hWnd)
    {
        if (!Win32.IsWindow(hWnd))
        {
            return;
        }

        Win32.ShowWindow(hWnd, Win32.SW_HIDE);
    }

    public void Show(IntPtr hWnd)
    {
        if (!Win32.IsWindow(hWnd))
        {
            return;
        }

        Win32.ShowWindow(hWnd, Win32.SW_SHOW);
        Win32.SetForegroundWindow(hWnd);
        HideFromTaskbarWithRetry(hWnd);
    }

    public void Restore(IntPtr hWnd)
    {
        if (!_states.TryGetValue(hWnd, out var state))
        {
            _logger.LogDebug("No stored state for window {Handle}.", hWnd);
            return;
        }

        RestoreState(state);
        _states.Remove(hWnd);
    }

    public void RestoreAll()
    {
        foreach (var state in _states.Values.ToList())
        {
            RestoreState(state);
        }

        _states.Clear();
    }

    private void RestoreState(ManagedWindowState state)
    {
        if (!Win32.IsWindow(state.Handle))
        {
            _logger.LogInformation("Skipping restore; window {Handle} no longer exists.", state.Handle);
            return;
        }

        RestoreTaskbarTab(state);
        var placement = state.Placement;
        placement.length = System.Runtime.InteropServices.Marshal.SizeOf<Win32.WINDOWPLACEMENT>();
        Win32.SetWindowPlacement(state.Handle, ref placement);
        Win32.ShowWindow(state.Handle, placement.showCmd == Win32.SW_HIDE ? Win32.SW_SHOW : placement.showCmd);
        _logger.LogInformation("Restored window {Handle}.", state.Handle);
    }

    private void HideFromTaskbarWithRetry(IntPtr hWnd)
    {
        HideFromTaskbar(hWnd);
        var queue = DispatcherQueue.GetForCurrentThread();
        if (queue is null)
        {
            return;
        }

        _ = RetryHideFromTaskbarAsync(queue, hWnd);
    }

    private async Task RetryHideFromTaskbarAsync(DispatcherQueue queue, IntPtr hWnd)
    {
        foreach (var delayMs in TaskbarHideRetryMs)
        {
            await Task.Delay(delayMs).ConfigureAwait(false);
            queue.TryEnqueue(() =>
            {
                if (_states.ContainsKey(hWnd) && Win32.IsWindow(hWnd))
                {
                    HideFromTaskbar(hWnd);
                }
            });
        }
    }

    private void HideFromTaskbar(IntPtr hWnd)
    {
        if (!_states.TryGetValue(hWnd, out var state) || !Win32.IsWindow(hWnd))
        {
            return;
        }

        var list = GetTaskbarList();
        if (list is null)
        {
            return;
        }

        try
        {
            list.DeleteTab(hWnd);
            state.TaskbarTabRemoved = true;
            _logger.LogDebug("Removed window {Handle} from the taskbar.", hWnd);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "DeleteTab failed for {Handle}.", hWnd);
        }
    }

    private void RestoreTaskbarTab(ManagedWindowState state)
    {
        if (!state.TaskbarTabRemoved)
        {
            return;
        }

        var list = GetTaskbarList();
        if (list is null)
        {
            return;
        }

        try
        {
            list.AddTab(state.Handle);
            state.TaskbarTabRemoved = false;
            _logger.LogDebug("Restored window {Handle} to the taskbar.", state.Handle);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "AddTab failed for {Handle}.", state.Handle);
        }
    }

    private Win32.ITaskbarList? GetTaskbarList()
    {
        if (_taskbarListFailed)
        {
            return _taskbarList;
        }

        if (_taskbarList is not null)
        {
            return _taskbarList;
        }

        try
        {
            var type = Type.GetTypeFromCLSID(Win32.CLSID_TaskbarList, throwOnError: true);
            var instance = Activator.CreateInstance(type!) as Win32.ITaskbarList;
            instance?.HrInit();
            _taskbarList = instance;
            if (_taskbarList is null)
            {
                _taskbarListFailed = true;
            }
        }
        catch (Exception ex)
        {
            _taskbarListFailed = true;
            _logger.LogWarning(ex, "Could not create ITaskbarList; hosted native apps may keep their taskbar buttons.");
        }

        return _taskbarList;
    }
}

internal sealed class ManagedWindowState
{
    public ManagedWindowState(IntPtr handle, Win32.WINDOWPLACEMENT placement)
    {
        Handle = handle;
        Placement = placement;
    }

    public IntPtr Handle { get; }

    public Win32.WINDOWPLACEMENT Placement { get; }

    public bool TaskbarTabRemoved { get; set; }
}

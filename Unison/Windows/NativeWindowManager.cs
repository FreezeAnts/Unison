using Microsoft.Extensions.Logging;

namespace Unison.Windows;

/// <summary>
/// Remembers original bounds/show state of managed main windows so they can be restored on exit.
/// Called by native adapters. Uses Win32 GetWindowPlacement/SetWindowPos. Never uses SetParent.
/// </summary>
public sealed class NativeWindowManager
{
    private readonly ILogger<NativeWindowManager> _logger;
    private readonly Dictionary<IntPtr, ManagedWindowState> _states = new();

    public NativeWindowManager(ILogger<NativeWindowManager> logger)
    {
        _logger = logger;
    }

    internal IReadOnlyDictionary<IntPtr, ManagedWindowState> TrackedWindows => _states;

    public void Remember(IntPtr hWnd)
    {
        if (_states.ContainsKey(hWnd))
        {
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

        var placement = state.Placement;
        placement.length = System.Runtime.InteropServices.Marshal.SizeOf<Win32.WINDOWPLACEMENT>();
        Win32.SetWindowPlacement(state.Handle, ref placement);
        Win32.ShowWindow(state.Handle, placement.showCmd == Win32.SW_HIDE ? Win32.SW_SHOW : placement.showCmd);
        _logger.LogInformation("Restored window {Handle}.", state.Handle);
    }
}

internal sealed record ManagedWindowState(IntPtr Handle, Win32.WINDOWPLACEMENT Placement);

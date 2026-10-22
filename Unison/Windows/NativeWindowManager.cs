using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;

namespace Unison.Windows;

/// <summary>
/// Remembers original bounds/show state of managed main windows so they can be restored on exit.
/// Called by native adapters. Uses Win32 GetWindowPlacement/SetWindowPos. Never uses SetParent:
/// reparenting an Electron/Chromium top-level window breaks its rendering. Instead a hosted
/// window stays top-level but is (a) positioned by its DWM extended frame bounds so the visible
/// frame exactly fills the host rect, (b) clipped with a window region so its shadow / invisible
/// resize border and any minimum-size overflow cannot paint over Unison's chrome, and
/// (c) owned by Unison (GWLP_HWNDPARENT) so it follows Unison in z-order and minimises with it.
/// Hosted windows are removed from the taskbar via ITaskbarList.DeleteTab while Unison manages them.
/// </summary>
public sealed class NativeWindowManager : IDisposable
{
    private static readonly int[] TaskbarHideRetryMs = [400, 1200];
    private const int Offscreen = -32000;

    private readonly ILogger<NativeWindowManager> _logger;
    private readonly Dictionary<IntPtr, ManagedWindowState> _states = new();
    private Win32.ITaskbarList? _taskbarList;
    private bool _taskbarListFailed;
    private IntPtr _unisonHost;
    private IntPtr _activeWindow;
    private HostRect _lastBounds;
    private bool _fitting;
    private bool _refitQueued;
    private bool _disposed;
    private DispatcherQueue? _dispatcherQueue;

    // WinEvent plumbing: keep delegates/handles alive for the lifetime of the manager.
    private Win32.WinEventDelegate? _winEventCallback;
    private IntPtr _locationHook;
    private IntPtr _minimizeHook;

    public NativeWindowManager(ILogger<NativeWindowManager> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Unison's main HWND. Set once from MainWindow so hosted apps can be owned by Unison
    /// and follow it in z-order. Also installs the WinEvent hooks used to keep the hosted
    /// window glued to the content pane.
    /// </summary>
    public void SetUnisonHost(IntPtr hWnd)
    {
        _unisonHost = hWnd;
        _dispatcherQueue ??= DispatcherQueue.GetForCurrentThread();
        InstallWinEventHooks();
    }

    internal IReadOnlyDictionary<IntPtr, ManagedWindowState> TrackedWindows => _states;

    public IntPtr ActiveWindow => _activeWindow;

    public HostRect LastBounds => _lastBounds;

    public void Remember(IntPtr hWnd)
    {
        if (_states.ContainsKey(hWnd))
        {
            HideFromTaskbar(hWnd);
            return;
        }

        var placement = new Win32.WINDOWPLACEMENT { length = Marshal.SizeOf<Win32.WINDOWPLACEMENT>() };
        if (!Win32.GetWindowPlacement(hWnd, ref placement))
        {
            Win32.GetWindowRect(hWnd, out var rect);
            placement.rcNormalPosition = rect;
            placement.showCmd = Win32.SW_SHOW;
        }

        var originalOwner = Win32.GetWindow(hWnd, Win32.GW_OWNER);
        var state = new ManagedWindowState(hWnd, placement, originalOwner);
        _states[hWnd] = state;
        var normal = placement.rcNormalPosition;
        _logger.LogInformation(
            "Remembered window {Handle} at {Left},{Top} {Width}x{Height} showCmd={ShowCmd} owner={Owner}.",
            hWnd,
            normal.Left,
            normal.Top,
            normal.Width,
            normal.Height,
            placement.showCmd,
            originalOwner);
        HideFromTaskbar(hWnd);
    }

    public void FitToRect(IntPtr hWnd, HostRect bounds)
    {
        if (!Win32.IsWindow(hWnd) || !bounds.IsValid)
        {
            _logger.LogWarning("Cannot fit window {Handle}; hwnd valid={Valid}, bounds={Bounds}.", hWnd, Win32.IsWindow(hWnd), bounds);
            return;
        }

        _lastBounds = bounds;
        if (_states.TryGetValue(hWnd, out var tracked))
        {
            tracked.IsParked = false;
        }

        // Owned windows hide with their owner, but an explicit park keeps the hosted
        // window invisible even if ownership could not be applied.
        if (_unisonHost != IntPtr.Zero && Win32.IsWindow(_unisonHost) && Win32.IsIconic(_unisonHost))
        {
            ConcealCore(hWnd, keepActive: true);
            return;
        }

        _activeWindow = hWnd;

        if (Win32.IsIconic(hWnd) || Win32.IsZoomed(hWnd))
        {
            Win32.ShowWindow(hWnd, Win32.SW_RESTORE);
        }

        if (!Win32.IsWindowVisible(hWnd))
        {
            Win32.ShowWindow(hWnd, Win32.SW_SHOWNA);
        }

        ApplyOwnership(hWnd);

        var outer = ComputeOuterRect(hWnd, bounds, out var insets);
        _fitting = true;
        try
        {
            Win32.SetWindowPos(
                hWnd,
                Win32.HWND_TOP,
                outer.Left,
                outer.Top,
                outer.Width,
                outer.Height,
                Win32.SWP_NOACTIVATE | Win32.SWP_SHOWWINDOW);
            ApplyClipRegion(hWnd, bounds, insets);
        }
        finally
        {
            _fitting = false;
        }

        if (_states.TryGetValue(hWnd, out var state))
        {
            state.LastOuterRect = outer;
        }

        RaiseUnisonThenHosted(hWnd);
        ForceRepaint(hWnd);
        HideFromTaskbarWithRetry(hWnd);
    }

    /// <summary>
    /// Legacy name kept for callers: parks off-screen instead of SW_HIDE. SW_HIDE leaves
    /// Chromium/Electron windows (and classic Outlook) blank when shown again.
    /// </summary>
    public void Hide(IntPtr hWnd) => Conceal(hWnd);

    /// <summary>
    /// Park off-screen instead of SW_HIDE so the next show does not come back blank.
    /// </summary>
    public void Conceal(IntPtr hWnd) => ConcealCore(hWnd, keepActive: false);

    private void ConcealCore(IntPtr hWnd, bool keepActive)
    {
        if (!Win32.IsWindow(hWnd))
        {
            return;
        }

        if (_states.TryGetValue(hWnd, out var state))
        {
            state.IsParked = true;
        }

        if (!keepActive && _activeWindow == hWnd)
        {
            _activeWindow = IntPtr.Zero;
        }

        _fitting = true;
        try
        {
            Win32.SetWindowPos(
                hWnd,
                Win32.HWND_BOTTOM,
                Offscreen,
                Offscreen,
                0,
                0,
                Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
        }
        finally
        {
            _fitting = false;
        }
    }

    public void Show(IntPtr hWnd)
    {
        if (!Win32.IsWindow(hWnd))
        {
            return;
        }

        if (_states.TryGetValue(hWnd, out var state))
        {
            state.IsParked = false;
        }

        if (Win32.IsIconic(hWnd) || Win32.IsZoomed(hWnd))
        {
            Win32.ShowWindow(hWnd, Win32.SW_RESTORE);
        }

        Win32.ShowWindow(hWnd, Win32.SW_SHOWNA);
        ForceRepaint(hWnd);
    }

    /// <summary>Re-fit the currently hosted window to the last known host bounds.</summary>
    public void RefitActive()
    {
        if (_activeWindow == IntPtr.Zero || !_lastBounds.IsValid)
        {
            return;
        }

        if (!Win32.IsWindow(_activeWindow))
        {
            _activeWindow = IntPtr.Zero;
            return;
        }

        // The active window is only ever parked by the minimise path, and
        // FitToRect un-parks it (it parks again itself if Unison is still iconic).
        FitToRect(_activeWindow, _lastBounds);
    }

    /// <summary>
    /// Bring Unison near the top, then the hosted window above it so the pane content is visible
    /// while the title/service bar (outside the host rect) stays usable.
    /// </summary>
    private void RaiseUnisonThenHosted(IntPtr hosted)
    {
        const uint flags = Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE;
        _fitting = true;
        try
        {
            if (_unisonHost != IntPtr.Zero && Win32.IsWindow(_unisonHost))
            {
                Win32.SetWindowPos(_unisonHost, Win32.HWND_TOP, 0, 0, 0, 0, flags);
            }

            if (Win32.IsWindow(hosted))
            {
                Win32.SetWindowPos(hosted, Win32.HWND_TOP, 0, 0, 0, 0, flags);
            }
        }
        finally
        {
            _fitting = false;
        }
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
        if (_activeWindow == hWnd)
        {
            _activeWindow = IntPtr.Zero;
        }
    }

    public void RestoreAll()
    {
        foreach (var state in _states.Values.ToList())
        {
            RestoreState(state);
        }

        _states.Clear();
        _activeWindow = IntPtr.Zero;
    }

    private void RestoreState(ManagedWindowState state)
    {
        if (!Win32.IsWindow(state.Handle))
        {
            _logger.LogInformation("Skipping restore; window {Handle} no longer exists.", state.Handle);
            return;
        }

        _fitting = true;
        try
        {
            // Undo hosting in reverse order: clip region, ownership, taskbar tab, placement.
            Win32.SetWindowRgn(state.Handle, IntPtr.Zero, true);
            RestoreOwnership(state);
        }
        finally
        {
            _fitting = false;
        }

        RestoreTaskbarTab(state);
        var placement = state.Placement;
        placement.length = Marshal.SizeOf<Win32.WINDOWPLACEMENT>();
        Win32.SetWindowPlacement(state.Handle, ref placement);
        Win32.ShowWindow(state.Handle, placement.showCmd == Win32.SW_HIDE ? Win32.SW_SHOW : placement.showCmd);
        ForceRepaint(state.Handle);
        _logger.LogInformation("Restored window {Handle}.", state.Handle);
    }

    private void ApplyOwnership(IntPtr hWnd)
    {
        if (_unisonHost == IntPtr.Zero || !Win32.IsWindow(_unisonHost) || _unisonHost == hWnd)
        {
            return;
        }

        if (!_states.TryGetValue(hWnd, out var state) || state.OwnershipApplied)
        {
            return;
        }

        var currentOwner = Win32.GetWindow(hWnd, Win32.GW_OWNER);
        if (currentOwner == _unisonHost)
        {
            state.OwnershipApplied = true;
            return;
        }

        // Only adopt unowned windows; never steal a window that already has an owner.
        if (currentOwner != IntPtr.Zero)
        {
            return;
        }

        var previous = Win32.SetWindowLongPtr(hWnd, Win32.GWLP_HWNDPARENT, _unisonHost);
        var error = Marshal.GetLastWin32Error();
        if (Win32.GetWindow(hWnd, Win32.GW_OWNER) == _unisonHost)
        {
            state.OwnershipApplied = true;
            _logger.LogDebug("Owned window {Handle} by Unison (previous return {Previous}, err {Error}).", hWnd, previous, error);
        }
        else
        {
            _logger.LogDebug("Could not own window {Handle}; owner is still {Owner} (err {Error}).", hWnd, Win32.GetWindow(hWnd, Win32.GW_OWNER), error);
        }
    }

    private void RestoreOwnership(ManagedWindowState state)
    {
        if (!state.OwnershipApplied)
        {
            return;
        }

        Win32.SetWindowLongPtr(state.Handle, Win32.GWLP_HWNDPARENT, state.OriginalOwner);
        state.OwnershipApplied = false;
    }

    /// <summary>
    /// Computes the outer window rect that puts the <em>visible</em> (extended frame) rect
    /// exactly on <paramref name="bounds"/>, and the insets between outer and visible rects.
    /// </summary>
    private static Win32.RECT ComputeOuterRect(IntPtr hWnd, HostRect bounds, out FrameInsets insets)
    {
        insets = FrameInsets.Zero;
        if (TryGetFrameInsets(hWnd, out var found))
        {
            insets = found;
        }

        return new Win32.RECT
        {
            Left = bounds.Left - insets.Left,
            Top = bounds.Top - insets.Top,
            Right = bounds.Left + bounds.Width + insets.Right,
            Bottom = bounds.Top + bounds.Height + insets.Bottom
        };
    }

    private static bool TryGetFrameInsets(IntPtr hWnd, out FrameInsets insets)
    {
        insets = FrameInsets.Zero;
        if (!Win32.GetWindowRect(hWnd, out var outer) || outer.Width <= 0 || outer.Height <= 0)
        {
            return false;
        }

        var size = Marshal.SizeOf<Win32.RECT>();
        if (Win32.DwmGetWindowAttribute(hWnd, Win32.DWMWA_EXTENDED_FRAME_BOUNDS, out Win32.RECT frame, size) != 0
            || frame.Width <= 0 || frame.Height <= 0)
        {
            return false;
        }

        insets = new FrameInsets(
            Math.Max(0, frame.Left - outer.Left),
            Math.Max(0, frame.Top - outer.Top),
            Math.Max(0, outer.Right - frame.Right),
            Math.Max(0, outer.Bottom - frame.Bottom));
        return true;
    }

    private static void ApplyClipRegion(IntPtr hWnd, HostRect bounds, FrameInsets insets)
    {
        // Region coordinates are relative to the window's outer top-left. The visible
        // frame starts at (insets.Left, insets.Top) inside that rect; clip to exactly it
        // so shadows/borders and min-size overflow never cover Unison's chrome.
        var region = Win32.CreateRectRgn(
            insets.Left,
            insets.Top,
            insets.Left + bounds.Width,
            insets.Top + bounds.Height);
        if (region == IntPtr.Zero)
        {
            return;
        }

        // On success the system owns the region handle.
        if (Win32.SetWindowRgn(hWnd, region, true) == 0)
        {
            Win32.DeleteObject(region);
        }
    }

    private static void ForceRepaint(IntPtr hWnd)
    {
        Win32.RedrawWindow(
            hWnd,
            IntPtr.Zero,
            IntPtr.Zero,
            Win32.RDW_INVALIDATE | Win32.RDW_UPDATENOW | Win32.RDW_ALLCHILDREN | Win32.RDW_FRAME);
    }

    private void InstallWinEventHooks()
    {
        if (_disposed || _locationHook != IntPtr.Zero)
        {
            return;
        }

        try
        {
            _winEventCallback = OnWinEvent;
            _locationHook = Win32.SetWinEventHook(
                Win32.EVENT_OBJECT_LOCATIONCHANGE,
                Win32.EVENT_OBJECT_LOCATIONCHANGE,
                IntPtr.Zero,
                _winEventCallback,
                0,
                0,
                Win32.WINEVENT_OUTOFCONTEXT);
            _minimizeHook = Win32.SetWinEventHook(
                Win32.EVENT_SYSTEM_MINIMIZESTART,
                Win32.EVENT_SYSTEM_MINIMIZEEND,
                IntPtr.Zero,
                _winEventCallback,
                0,
                0,
                Win32.WINEVENT_OUTOFCONTEXT);
            if (_locationHook == IntPtr.Zero && _minimizeHook == IntPtr.Zero)
            {
                _logger.LogWarning("Could not install WinEvent hooks; hosted windows will only re-fit on Unison resize/move.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not install WinEvent hooks.");
        }
    }

    private void OnWinEvent(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint eventThread, uint eventTime)
    {
        if (_disposed || _fitting || hwnd == IntPtr.Zero)
        {
            return;
        }

        if (idObject != Win32.OBJID_WINDOW || idChild != Win32.CHILDID_SELF)
        {
            return;
        }

        if (eventType == Win32.EVENT_SYSTEM_MINIMIZESTART && hwnd == _unisonHost)
        {
            if (_activeWindow != IntPtr.Zero && Win32.IsWindow(_activeWindow))
            {
                ConcealCore(_activeWindow, keepActive: true);
            }

            return;
        }

        if (eventType == Win32.EVENT_SYSTEM_MINIMIZEEND && hwnd == _unisonHost)
        {
            QueueRefit();
            return;
        }

        if (eventType != Win32.EVENT_OBJECT_LOCATIONCHANGE || hwnd != _activeWindow || !_lastBounds.IsValid)
        {
            return;
        }

        // The app moved/resized itself (Teams restores its saved size, users can drag
        // the hosted title bar). Pull it back unless it already sits exactly on the
        // pane; that equality check also stops echo loops from our own SetWindowPos
        // and from apps whose minimum size is larger than the pane.
        if (!Win32.GetWindowRect(hwnd, out var current))
        {
            return;
        }

        var wanted = ComputeOuterRect(hwnd, _lastBounds, out _);
        if (RectsMatch(current, wanted))
        {
            return;
        }

        if (_states.TryGetValue(hwnd, out var state) && state.LastOuterRect is { } last && RectsMatch(current, last))
        {
            // Already at the rect we last enforced (app cannot get closer, e.g. min size).
            return;
        }

        QueueRefit();
    }

    private static bool RectsMatch(Win32.RECT a, Win32.RECT b) =>
        Math.Abs(a.Left - b.Left) <= 1
        && Math.Abs(a.Top - b.Top) <= 1
        && Math.Abs(a.Width - b.Width) <= 1
        && Math.Abs(a.Height - b.Height) <= 1;

    private void QueueRefit()
    {
        if (_refitQueued || _disposed)
        {
            return;
        }

        var queue = _dispatcherQueue ?? DispatcherQueue.GetForCurrentThread();
        if (queue is null)
        {
            RefitActive();
            return;
        }

        _refitQueued = true;
        queue.TryEnqueue(() =>
        {
            _refitQueued = false;
            if (!_disposed)
            {
                RefitActive();
            }
        });
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_locationHook != IntPtr.Zero)
        {
            Win32.UnhookWinEvent(_locationHook);
            _locationHook = IntPtr.Zero;
        }

        if (_minimizeHook != IntPtr.Zero)
        {
            Win32.UnhookWinEvent(_minimizeHook);
            _minimizeHook = IntPtr.Zero;
        }
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

internal readonly record struct FrameInsets(int Left, int Top, int Right, int Bottom)
{
    public static FrameInsets Zero => new(0, 0, 0, 0);
}

internal sealed class ManagedWindowState
{
    public ManagedWindowState(IntPtr handle, Win32.WINDOWPLACEMENT placement, IntPtr originalOwner)
    {
        Handle = handle;
        Placement = placement;
        OriginalOwner = originalOwner;
    }

    public IntPtr Handle { get; }

    public Win32.WINDOWPLACEMENT Placement { get; }

    public IntPtr OriginalOwner { get; }

    public bool TaskbarTabRemoved { get; set; }

    public bool OwnershipApplied { get; set; }

    public bool IsParked { get; set; }

    public Win32.RECT? LastOuterRect { get; set; }
}

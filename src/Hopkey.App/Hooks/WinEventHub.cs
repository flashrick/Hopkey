using System.Collections.Concurrent;
using System.Windows.Threading;
using Hopkey.App.Infrastructure;
using Hopkey.Core.Native;
using Hopkey.Core.Util;

namespace Hopkey.App.Hooks;

/// <summary>
/// Owns all SetWinEventHook hooks on the CL-Hooks thread. Callbacks only filter (OBJID_WINDOW/CHILDID_SELF + tracked set)
/// and forward to the UI dispatcher. Location-change hooks are installed per attached dialog process only.
/// </summary>
public sealed class WinEventHub : IDisposable
{
    private readonly DispatcherThread _thread;
    private readonly Dispatcher _ui;
    private readonly User32.WinEventDelegate _proc; // keep alive
    private readonly List<IntPtr> _hooks = new();
    private IntPtr _locationHook;
    private volatile IntPtr _locationTarget;
    private int _locationPending;
    private readonly ConcurrentDictionary<IntPtr, byte> _tracked = new();
    private readonly ConcurrentDictionary<IntPtr, byte> _explorers = new();

    public event Action<IntPtr>? Foreground;
    public event Action<IntPtr>? Destroyed;
    public event Action<IntPtr>? Hidden;
    public event Action<IntPtr>? Shown;
    public event Action<IntPtr>? DialogEnded;
    public event Action<IntPtr>? MinimizeStart;
    public event Action<IntPtr>? MinimizeEnd;
    public event Action<IntPtr>? LocationChanged;
    public event Action<IntPtr>? ExplorerNameChanged;

    public WinEventHub(DispatcherThread hookThread, Dispatcher ui)
    {
        _thread = hookThread;
        _ui = ui;
        _proc = Callback;
    }

    public void Start() => _thread.Dispatcher.Invoke(() =>
    {
        if (_hooks.Count > 0) return;
        const uint flags = User32.WINEVENT_OUTOFCONTEXT | User32.WINEVENT_SKIPOWNPROCESS;
        Add(User32.SetWinEventHook(User32.EVENT_SYSTEM_FOREGROUND, User32.EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _proc, 0, 0, flags));
        Add(User32.SetWinEventHook(User32.EVENT_SYSTEM_DIALOGEND, User32.EVENT_SYSTEM_DIALOGEND, IntPtr.Zero, _proc, 0, 0, flags));
        Add(User32.SetWinEventHook(User32.EVENT_SYSTEM_MINIMIZESTART, User32.EVENT_SYSTEM_MINIMIZEEND, IntPtr.Zero, _proc, 0, 0, flags));
        Add(User32.SetWinEventHook(User32.EVENT_OBJECT_DESTROY, User32.EVENT_OBJECT_HIDE, IntPtr.Zero, _proc, 0, 0, flags));
        Add(User32.SetWinEventHook(User32.EVENT_OBJECT_NAMECHANGE, User32.EVENT_OBJECT_NAMECHANGE, IntPtr.Zero, _proc, 0, 0, flags));
        Logger.Info($"WinEvent hooks installed: {_hooks.Count}");
    });

    private void Add(IntPtr h)
    {
        if (h != IntPtr.Zero) _hooks.Add(h);
        else Logger.Warn("SetWinEventHook failed");
    }

    public void Stop() => _thread.Dispatcher.Invoke(() =>
    {
        foreach (var h in _hooks) User32.UnhookWinEvent(h);
        _hooks.Clear();
        DetachLocation();
    });

    /// <summary>Track a window so DESTROY/HIDE/minimize/dialog-end events for it are forwarded.</summary>
    public void Track(IntPtr hwnd) => _tracked[hwnd] = 0;
    public void Untrack(IntPtr hwnd) => _tracked.TryRemove(hwnd, out _);
    public void TrackExplorer(IntPtr hwnd) => _explorers[hwnd] = 0;

    /// <summary>Install a LOCATIONCHANGE hook scoped to the dialog's process/thread.</summary>
    public void AttachLocation(IntPtr hwnd) => _thread.Dispatcher.BeginInvoke(() =>
    {
        DetachLocation();
        uint tid = User32.GetWindowThreadProcessId(hwnd, out uint pid);
        _locationTarget = hwnd;
        _locationHook = User32.SetWinEventHook(User32.EVENT_OBJECT_LOCATIONCHANGE, User32.EVENT_OBJECT_LOCATIONCHANGE, IntPtr.Zero, _proc, pid, tid,
            User32.WINEVENT_OUTOFCONTEXT | User32.WINEVENT_SKIPOWNPROCESS);
    });

    public void DetachLocationAsync() => _thread.Dispatcher.BeginInvoke(DetachLocation);

    private void DetachLocation()
    {
        if (_locationHook != IntPtr.Zero) User32.UnhookWinEvent(_locationHook);
        _locationHook = IntPtr.Zero;
        _locationTarget = IntPtr.Zero;
    }

    private void Callback(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        try
        {
            if (idObject != User32.OBJID_WINDOW || idChild != User32.CHILDID_SELF || hwnd == IntPtr.Zero) return;
            switch (ev)
            {
                case User32.EVENT_SYSTEM_FOREGROUND:
                    _ui.BeginInvoke(DispatcherPriority.Send, () => Foreground?.Invoke(hwnd));
                    break;
                case User32.EVENT_OBJECT_LOCATIONCHANGE:
                    if (hwnd != _locationTarget) return;
                    if (Interlocked.Exchange(ref _locationPending, 1) == 0)
                        _ui.BeginInvoke(DispatcherPriority.Render, () =>
                        {
                            Interlocked.Exchange(ref _locationPending, 0);
                            LocationChanged?.Invoke(hwnd);
                        });
                    break;
                case User32.EVENT_OBJECT_DESTROY:
                    WindowInfoCache.Invalidate(hwnd);
                    _explorers.TryRemove(hwnd, out _);
                    if (_tracked.ContainsKey(hwnd)) _ui.BeginInvoke(() => Destroyed?.Invoke(hwnd));
                    break;
                case User32.EVENT_OBJECT_HIDE:
                    if (_tracked.ContainsKey(hwnd)) _ui.BeginInvoke(() => Hidden?.Invoke(hwnd));
                    break;
                case User32.EVENT_OBJECT_SHOW:
                    if (_tracked.ContainsKey(hwnd)) _ui.BeginInvoke(() => Shown?.Invoke(hwnd));
                    break;
                case User32.EVENT_SYSTEM_DIALOGEND:
                    if (_tracked.ContainsKey(hwnd)) _ui.BeginInvoke(() => DialogEnded?.Invoke(hwnd));
                    break;
                case User32.EVENT_SYSTEM_MINIMIZESTART:
                    if (_tracked.ContainsKey(hwnd)) _ui.BeginInvoke(() => MinimizeStart?.Invoke(hwnd));
                    break;
                case User32.EVENT_SYSTEM_MINIMIZEEND:
                    if (_tracked.ContainsKey(hwnd)) _ui.BeginInvoke(() => MinimizeEnd?.Invoke(hwnd));
                    break;
                case User32.EVENT_OBJECT_NAMECHANGE:
                    if (_explorers.ContainsKey(hwnd)) _ui.BeginInvoke(() => ExplorerNameChanged?.Invoke(hwnd));
                    break;
            }
        }
        catch (Exception ex) { Logger.Error("WinEvent callback failed", ex); }
    }

    public void Dispose() => Stop();
}

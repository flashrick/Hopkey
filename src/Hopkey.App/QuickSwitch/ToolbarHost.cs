using System.Windows;
using Hopkey.App.Infrastructure;
using Hopkey.Core.Native;
using Hopkey.Core.Util;

namespace Hopkey.App.QuickSwitch;

/// <summary>
/// Hosts the QuickSwitch toolbar on its own CL-Toolbar thread. Being owned cross-process by the dialog implicitly
/// attaches input queues; isolating the toolbar keeps a hung target from freezing the launcher/tray.
/// Ownership is dropped before hide/minimize/dialog end so Windows doesn't destroy the toolbar with its owner.
/// </summary>
public sealed class ToolbarHost : IDisposable
{
    private readonly DispatcherThread _thread;
    private readonly IQuickSwitchToolbarActions _actions;
    private QuickSwitchToolbar? _window;
    private IntPtr _owner;
    private bool _unowned; // owner could not be set (elevated dialog) → topmost fallback

    private static void SetTopmost(QuickSwitchToolbar w, bool on) =>
        User32.SetWindowPos(w.Handle, on ? User32.HWND_TOPMOST : User32.HWND_NOTOPMOST, 0, 0, 0, 0,
            User32.SWP_NOMOVE | User32.SWP_NOSIZE | User32.SWP_NOACTIVATE);

    /// <summary>
    /// Another window took the foreground. An owned toolbar follows its dialog in z-order by itself; the topmost
    /// fallback (unowned) toolbar has to be hidden explicitly. It is shown again when the dialog is re-activated.
    /// </summary>
    public void OwnerDeactivated() => _thread.Post(() =>
    {
        if (_unowned && _window is { IsVisible: true } w && w.MenusOpen == 0) w.Hide();
    });

    public ToolbarHost(IQuickSwitchToolbarActions actions)
    {
        _actions = actions;
        _thread = new DispatcherThread("CL-Toolbar");
        // Pre-create hidden so the first attach is instant.
        _thread.Post(() => Ensure());
    }

    private QuickSwitchToolbar Ensure()
    {
        if (_window != null) return _window;
        var w = new QuickSwitchToolbar(_actions);
        w.Closed += (_, _) => { if (ReferenceEquals(_window, w)) { _window = null; _owner = IntPtr.Zero; } };
        w.SizeChanged += (_, _) => RepositionCore();
        new System.Windows.Interop.WindowInteropHelper(w).EnsureHandle();
        _window = w;
        return w;
    }

    public void AttachTo(FileDialogInfo dlg, string? recommended) => _thread.Post(() =>
    {
        var start = PerfLog.Start();
        try
        {
            var w = Ensure();
            if (!User32.IsWindow(dlg.Hwnd)) return;
            if (_owner != dlg.Hwnd)
            {
                User32.SetWindowLongPtr(w.Handle, User32.GWLP_HWNDPARENT, dlg.Hwnd);
                _owner = dlg.Hwnd;
                // UIPI: a non-elevated window cannot be owned by an elevated one (the call fails silently). Without an
                // owner the toolbar would sink behind other windows, so keep it topmost while its dialog is active
                // and hide it whenever another app takes the foreground (see OwnerDeactivated).
                bool unowned = User32.GetWindow(w.Handle, User32.GW_OWNER) != dlg.Hwnd;
                if (unowned != _unowned) SetTopmost(w, unowned);
                _unowned = unowned;
                if (unowned) Logger.Debug($"Toolbar can't be owned by 0x{dlg.Hwnd:X} (UIPI); topmost fallback");
            }
            w.SetRecommended(recommended);
            w.SetElevatedWarning(dlg.TargetElevated);
            w.SetBusy(false);
            if (!w.IsVisible)
            {
                w.Show();
                // Never keep the foreground: hand activation straight back to the dialog if Show() took it.
                if (User32.GetForegroundWindow() == w.Handle) User32.SetForegroundWindow(dlg.Hwnd);
            }
            RepositionCore();
            // SizeToContent finishes after Show(); WPF then moves the window to its own Left/Top. Re-place after layout.
            _thread.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, RepositionCore);
            _thread.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ContextIdle, RepositionCore);
            PerfLog.Stop("quickswitch.toolbar", start);
        }
        catch (Exception ex) { Logger.Warn("Toolbar attach failed", ex); }
    });

    public void Reposition() => _thread.Post(RepositionCore);

    private void RepositionCore()
    {
        var w = _window;
        if (w == null || _owner == IntPtr.Zero || !User32.IsWindow(_owner)) return;
        var dlg = Dwmapi.GetExtendedFrameBounds(_owner);
        var work = User32.GetWorkAreaForWindow(_owner);
        User32.GetWindowRect(w.Handle, out var self);
        int gap = (int)Math.Round(4 * User32.GetDpiForWindow(_owner) / 96.0);
        var (x, y, side) = ToolbarPlacement.Compute(dlg, work, Math.Max(1, self.Width), Math.Max(1, self.Height), gap);
        w.Side = side;
        w.DesiredPosition = (x, y);
        w.OwnerDialog = _owner;
        User32.SetWindowPos(w.Handle, IntPtr.Zero, x, y, 0, 0, User32.SWP_NOSIZE | User32.SWP_NOZORDER | User32.SWP_NOACTIVATE);
    }

    public void Detach() => _thread.Post(() =>
    {
        var w = _window;
        if (w == null) return;
        w.DesiredPosition = null;
        w.OwnerDialog = IntPtr.Zero;
        if (_owner != IntPtr.Zero)
        {
            User32.SetWindowLongPtr(w.Handle, User32.GWLP_HWNDPARENT, IntPtr.Zero);
            _owner = IntPtr.Zero;
        }
        if (_unowned) { SetTopmost(w, false); _unowned = false; }
        if (w.IsVisible) w.Hide();
    });

    public void SetBusy(bool busy) => _thread.Post(() => _window?.SetBusy(busy));
    public void FlashError(string message) => _thread.Post(() => _window?.FlashError(message));
    public void SetRecommended(string? path) => _thread.Post(() => _window?.SetRecommended(path));

    public void RefreshResources() => _thread.Post(() =>
    {
        if (_window == null) return;
        _window.ApplyResources();
        _window.UpdateLayout();
        RepositionCore();
    });

    public void Dispose()
    {
        try
        {
            _thread.Dispatcher.Invoke(() =>
            {
                if (_window != null)
                {
                    User32.SetWindowLongPtr(_window.Handle, User32.GWLP_HWNDPARENT, IntPtr.Zero);
                    _window.Close();
                }
            }, System.Windows.Threading.DispatcherPriority.Send, CancellationToken.None, TimeSpan.FromSeconds(1));
        }
        catch { }
        _thread.Dispose();
    }
}

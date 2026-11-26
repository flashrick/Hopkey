using System.Windows.Interop;
using System.Windows.Threading;
using Hopkey.Core.Config;
using Hopkey.Core.Native;
using Hopkey.Core.Util;

namespace Hopkey.App.Hooks;

/// <summary>
/// Launcher hotkeys: double-tap Ctrl (LL hook state machine, runs on the hook thread) and RegisterHotKey combos
/// (message-only HwndSource on the UI thread). Also the in-dialog jump hotkey (default Ctrl+G), swallowed only when
/// a tracked file dialog is in the foreground.
/// </summary>
public sealed class HotkeyService : IDisposable
{
    private const int IdLauncher = 0xC100;
    private const int IdLauncherAlt = 0xC101;

    private readonly KeyboardHook _keyboard;
    private readonly Dispatcher _ui;
    private HwndSource? _source;
    private readonly Func<KeyEvent, bool> _filter;

    // double-ctrl state (hook thread only)
    private bool _ctrlDown;
    private uint _ctrlDownAt;
    private bool _otherKeyDuringCtrl;
    private uint _lastTapUpAt;
    private bool _lastTapValid;

    private volatile bool _doubleCtrlEnabled;
    /// <summary>Max gap between the two taps (ms). Override for automated UI tests via HOPKEY_DOUBLETAP_MS.</summary>
    private static readonly uint s_doubleTapMs =
        uint.TryParse(Environment.GetEnvironmentVariable("HOPKEY_DOUBLETAP_MS"), out var ms) && ms is >= 150 and <= 2000 ? ms : 300;
    private volatile bool _dialogJumpEnabled;
    private HotkeySpec _dialogJump = HotkeySpec.Parse("Ctrl+G");
    private readonly List<int> _registered = new();

    /// <summary>Set by QuickSwitchService: the dialog currently in the foreground (or zero).</summary>
    public volatile IntPtr ActiveDialog;

    public event Action? LauncherRequested;
    public event Action<IntPtr>? DialogJumpRequested;
    /// <summary>Raised when a RegisterHotKey combination could not be registered.</summary>
    public event Action<string>? RegistrationFailed;

    public HotkeyService(KeyboardHook keyboard, Dispatcher ui)
    {
        _keyboard = keyboard;
        _ui = ui;
        _filter = OnKey;
        _keyboard.AddFilter(_filter);
    }

    public void Apply(HotkeySettings s, QuickSwitchSettings qs, bool paused)
    {
        EnsureSource();
        foreach (var id in _registered) User32.UnregisterHotKey(_source!.Handle, id);
        _registered.Clear();

        var main = HotkeySpec.Parse(s.Launcher);
        _doubleCtrlEnabled = !paused && main.IsDoubleCtrl;
        _dialogJump = HotkeySpec.Parse(s.DialogJump);
        _dialogJumpEnabled = !paused && qs.Enabled && qs.DialogJumpHotkey && _dialogJump.IsValid && !_dialogJump.IsDoubleCtrl;
        if (paused) return;

        if (main.IsValid && !main.IsDoubleCtrl) Register(IdLauncher, main);
        var alt = HotkeySpec.Parse(s.LauncherAlt);
        if (alt.IsValid && !alt.IsDoubleCtrl) Register(IdLauncherAlt, alt);
    }

    /// <summary>Check whether a combination can be registered (used by the settings UI).</summary>
    public bool TryProbe(HotkeySpec spec)
    {
        if (!spec.IsValid || spec.IsDoubleCtrl) return spec.IsDoubleCtrl;
        EnsureSource();
        const int probeId = 0xC1FF;
        bool ok = User32.RegisterHotKey(_source!.Handle, probeId, spec.Modifiers | User32.MOD_NOREPEAT, spec.VirtualKey);
        if (ok) User32.UnregisterHotKey(_source.Handle, probeId);
        return ok;
    }

    private void Register(int id, HotkeySpec spec)
    {
        if (User32.RegisterHotKey(_source!.Handle, id, spec.Modifiers | User32.MOD_NOREPEAT, spec.VirtualKey))
        {
            _registered.Add(id);
        }
        else
        {
            Logger.Warn($"RegisterHotKey failed for {spec}");
            // Only the primary launcher hotkey is worth a balloon; the optional secondary one just logs.
            if (id == IdLauncher) RegistrationFailed?.Invoke(spec.ToString());
        }
    }

    private void EnsureSource()
    {
        if (_source != null) return;
        var p = new HwndSourceParameters("Hopkey.Hotkeys") { WindowStyle = 0, ParentWindow = new IntPtr(-3) /* HWND_MESSAGE */ };
        _source = new HwndSource(p);
        _source.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == User32.WM_HOTKEY)
        {
            int id = wParam.ToInt32();
            if (id is IdLauncher or IdLauncherAlt)
            {
                handled = true;
                LauncherRequested?.Invoke();
            }
        }
        return IntPtr.Zero;
    }

    private static bool IsCtrl(uint vk) => vk is User32.VK_CONTROL or User32.VK_LCONTROL or User32.VK_RCONTROL;

    /// <summary>Runs on the hook thread. Must be fast.</summary>
    private bool OnKey(KeyEvent e)
    {
        // ---- in-dialog jump hotkey ----
        if (e.IsDown && _dialogJumpEnabled && ActiveDialog != IntPtr.Zero && e.Vk == _dialogJump.VirtualKey)
        {
            var spec = _dialogJump;
            bool ctrl = User32.IsKeyDown(User32.VK_CONTROL), alt = User32.IsKeyDown(User32.VK_MENU), shift = User32.IsKeyDown(User32.VK_SHIFT);
            bool win = User32.IsKeyDown(User32.VK_LWIN) || User32.IsKeyDown(User32.VK_RWIN);
            bool match = ctrl == ((spec.Modifiers & User32.MOD_CONTROL) != 0) && alt == ((spec.Modifiers & User32.MOD_ALT) != 0)
                         && shift == ((spec.Modifiers & User32.MOD_SHIFT) != 0) && win == ((spec.Modifiers & User32.MOD_WIN) != 0);
            if (match)
            {
                var dlg = ActiveDialog;
                if (User32.GetForegroundWindow() == dlg)
                {
                    _ui.BeginInvoke(() => DialogJumpRequested?.Invoke(dlg));
                    return true;
                }
            }
        }

        // ---- double-tap Ctrl ----
        if (!_doubleCtrlEnabled) return false;
        if (IsCtrl(e.Vk))
        {
            if (Logger.MinLevel == LogLevel.Debug)
                Logger.Debug($"Ctrl vk=0x{e.Vk:X} {(e.IsDown ? "down" : "up")} t={e.Time} injected={e.Injected} lastTapValid={_lastTapValid} lastUp={_lastTapUpAt} downAt={_ctrlDownAt}");
            if (e.IsDown)
            {
                if (_ctrlDown) return false; // auto-repeat
                _ctrlDown = true;
                _ctrlDownAt = e.Time;
                _otherKeyDuringCtrl = false;
                if (_lastTapValid && e.Time - _lastTapUpAt > s_doubleTapMs) _lastTapValid = false;
            }
            else
            {
                _ctrlDown = false;
                bool quickTap = !_otherKeyDuringCtrl && e.Time - _ctrlDownAt < 250;
                if (!quickTap) { _lastTapValid = false; return false; }
                if (_lastTapValid && _ctrlDownAt - _lastTapUpAt < s_doubleTapMs)
                {
                    _lastTapValid = false;
                    _ui.BeginInvoke(DispatcherPriority.Send, () => LauncherRequested?.Invoke());
                }
                else
                {
                    _lastTapValid = true;
                    _lastTapUpAt = e.Time;
                }
            }
        }
        else if (e.IsDown)
        {
            if (_ctrlDown) _otherKeyDuringCtrl = true;
            _lastTapValid = false;
        }
        return false;
    }

    public void Dispose()
    {
        _keyboard.RemoveFilter(_filter);
        if (_source != null)
        {
            foreach (var id in _registered) User32.UnregisterHotKey(_source.Handle, id);
            _source.Dispose();
        }
    }
}

using System.Runtime.InteropServices;
using Hopkey.App.Infrastructure;
using Hopkey.Core.Native;
using Hopkey.Core.Util;

namespace Hopkey.App.Hooks;

public readonly record struct KeyEvent(uint Vk, uint ScanCode, bool IsDown, uint Time, bool Injected);

/// <summary>
/// WH_KEYBOARD_LL hook on the CL-Hooks thread. Registered filters run inside the callback and must be tiny
/// (target &lt;50µs, no COM, no allocation); returning true swallows the key.
/// The hook is silently removed by Windows if it times out (LowLevelHooksTimeout) — call Reinstall() on resume/unlock.
/// </summary>
public sealed class KeyboardHook : IDisposable
{
    private readonly DispatcherThread _thread;
    private readonly User32.LowLevelHookProc _proc;
    private IntPtr _hook;
    private volatile Func<KeyEvent, bool>[] _filters = Array.Empty<Func<KeyEvent, bool>>();

    public bool IsInstalled => _hook != IntPtr.Zero;

    public KeyboardHook(DispatcherThread hookThread)
    {
        _thread = hookThread;
        _proc = Callback;
    }

    public void AddFilter(Func<KeyEvent, bool> filter)
    {
        lock (this) _filters = _filters.Append(filter).ToArray();
    }

    public void RemoveFilter(Func<KeyEvent, bool> filter)
    {
        lock (this) _filters = _filters.Where(f => f != filter).ToArray();
    }

    public void Install() => _thread.Dispatcher.Invoke(() =>
    {
        if (_hook != IntPtr.Zero) return;
        _hook = User32.SetWindowsHookEx(User32.WH_KEYBOARD_LL, _proc, Kernel32.GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero) Logger.Error($"SetWindowsHookEx(WH_KEYBOARD_LL) failed: {Marshal.GetLastWin32Error()}");
        else Logger.Info("Keyboard hook installed");
    });

    public void Uninstall() => _thread.Dispatcher.Invoke(() =>
    {
        if (_hook == IntPtr.Zero) return;
        User32.UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    });

    public void Reinstall()
    {
        Uninstall();
        Install();
    }

    private IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode == User32.HC_ACTION)
        {
            try
            {
                KBDLLHOOKSTRUCT k;
                unsafe { k = *(KBDLLHOOKSTRUCT*)lParam; }
                int msg = (int)wParam;
                bool down = msg is User32.WM_KEYDOWN or User32.WM_SYSKEYDOWN;
                bool injected = (k.flags & User32.LLKHF_INJECTED) != 0;
                if (!(injected && k.dwExtraInfo == ForegroundHelper.InjectedMarker))
                {
                    var ev = new KeyEvent(k.vkCode, k.scanCode, down, k.time, injected);
                    var filters = _filters;
                    for (int i = 0; i < filters.Length; i++)
                    {
                        if (filters[i](ev)) return (IntPtr)1;
                    }
                }
            }
            catch (Exception ex) { Logger.Error("Keyboard hook filter failed", ex); }
        }
        return User32.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    public void Dispose() => Uninstall();
}

using System.Runtime.InteropServices;
using System.Windows.Threading;
using Hopkey.App.Hooks;
using Hopkey.Core.Native;

namespace Hopkey.App.Explorer;

/// <summary>
/// Type-to-search: when an Explorer window or a known file dialog is in the foreground and the user types a printable
/// character while focus is in the file view (DirectUIHWND / SysListView32), swallow it and open the launcher in
/// context mode. Runs inside the keyboard hook — must stay cheap.
/// </summary>
public sealed class TypeToSearchService : IDisposable
{
    private readonly KeyboardHook _keyboard;
    private readonly Dispatcher _ui;
    private readonly Func<KeyEvent, bool> _filter;
    private readonly char[] _buf = new char[4];
    private readonly byte[] _state = new byte[256];
    private volatile bool _enabled;

    /// <summary>Set by QuickSwitchService (thread-safe read in hook).</summary>
    public volatile IntPtr ActiveDialog;

    /// <summary>(foreground window, first character)</summary>
    public event Action<IntPtr, string>? Triggered;

    public TypeToSearchService(KeyboardHook keyboard, Dispatcher ui)
    {
        _keyboard = keyboard;
        _ui = ui;
        _filter = OnKey;
        _keyboard.AddFilter(_filter);
    }

    public void Apply(bool enabled) => _enabled = enabled;

    private bool OnKey(KeyEvent e)
    {
        if (!_enabled || !e.IsDown || e.Injected) return false;
        uint vk = e.Vk;
        // Printable keys only: digits, letters, numpad digits, OEM punctuation.
        bool printable = vk is >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A or >= 0x60 and <= 0x69 or >= 0xBA and <= 0xC0 or >= 0xDB and <= 0xDF;
        if (!printable) return false;
        if (User32.IsKeyDown(User32.VK_CONTROL) || User32.IsKeyDown(User32.VK_MENU) || User32.IsKeyDown(User32.VK_LWIN) || User32.IsKeyDown(User32.VK_RWIN))
            return false;

        IntPtr fg = User32.GetForegroundWindow();
        if (fg == IntPtr.Zero) return false;
        bool isExplorer = User32.ClassNameEquals(fg, "CabinetWClass") || User32.ClassNameEquals(fg, "ExploreWClass");
        if (!isExplorer && fg != ActiveDialog) return false;

        uint tid = User32.GetWindowThreadProcessId(fg, out _);
        var gti = new GUITHREADINFO { cbSize = Marshal.SizeOf<GUITHREADINFO>() };
        if (!User32.GetGUIThreadInfo(tid, ref gti)) return false;
        if ((gti.flags & (User32.GUI_INMENUMODE | User32.GUI_POPUPMENUMODE)) != 0) return false;
        if (gti.hwndCaret != IntPtr.Zero) return false; // an edit (rename box, address bar…) owns the caret
        var focus = gti.hwndFocus;
        if (focus == IntPtr.Zero) return false;
        if (!(User32.ClassNameEquals(focus, "DirectUIHWND") || User32.ClassNameEquals(focus, "SysListView32"))) return false;
        // DirectUIHWND is also used by the Win11 navigation pane? Ensure the view is inside SHELLDLL_DefView.
        var parent = User32.GetParent(focus);
        if (parent == IntPtr.Zero || !User32.ClassNameEquals(parent, "SHELLDLL_DefView")) return false;

        string text;
        if (vk is >= 0x41 and <= 0x5A) text = ((char)(vk + 32)).ToString();
        else if (vk is >= 0x30 and <= 0x39 && !User32.IsKeyDown(User32.VK_SHIFT)) text = ((char)vk).ToString();
        else if (vk is >= 0x60 and <= 0x69) text = ((char)('0' + (vk - 0x60))).ToString();
        else
        {
            Array.Clear(_state);
            if (User32.IsKeyDown(User32.VK_SHIFT)) _state[User32.VK_SHIFT] = 0x80;
            int n = User32.ToUnicodeEx(vk, e.ScanCode, _state, _buf, _buf.Length, 0x4, User32.GetKeyboardLayout(tid));
            if (n != 1 || char.IsControl(_buf[0])) return false;
            text = _buf[0].ToString();
        }
        _ui.BeginInvoke(DispatcherPriority.Send, () => Triggered?.Invoke(fg, text));
        return true;
    }

    public void Dispose() => _keyboard.RemoveFilter(_filter);
}

using Hopkey.Core.Native;
using Hopkey.Core.Util;

namespace Hopkey.App.Infrastructure;

/// <summary>Reliable foreground activation (AttachThreadInput trick + Alt key fallback).</summary>
public static class ForegroundHelper
{
    /// <summary>Marker placed in dwExtraInfo of our own injected input so our keyboard hook ignores it.</summary>
    public static readonly IntPtr InjectedMarker = new(0x434C4C49); // "CLLI"

    public static bool Activate(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        if (User32.GetForegroundWindow() == hwnd) return true;
        if (User32.IsIconic(hwnd)) User32.ShowWindow(hwnd, User32.SW_RESTORE);

        if (User32.SetForegroundWindow(hwnd) && User32.GetForegroundWindow() == hwnd) return true;

        IntPtr fg = User32.GetForegroundWindow();
        uint fgThread = User32.GetWindowThreadProcessId(fg, out _);
        uint myThread = Kernel32.GetCurrentThreadId();
        uint targetThread = User32.GetWindowThreadProcessId(hwnd, out _);
        bool a1 = false, a2 = false;
        try
        {
            if (fgThread != myThread) a1 = User32.AttachThreadInput(myThread, fgThread, true);
            if (targetThread != myThread && targetThread != fgThread) a2 = User32.AttachThreadInput(myThread, targetThread, true);
            User32.BringWindowToTop(hwnd);
            User32.SetForegroundWindow(hwnd);
        }
        finally
        {
            if (a1) User32.AttachThreadInput(myThread, fgThread, false);
            if (a2) User32.AttachThreadInput(myThread, targetThread, false);
        }
        if (User32.GetForegroundWindow() == hwnd) return true;

        // Fallback: a synthetic Alt press grants foreground rights.
        var inputs = new[] { User32.KeyInput(User32.VK_MENU, false, InjectedMarker), User32.KeyInput(User32.VK_MENU, true, InjectedMarker) };
        User32.SendInput((uint)inputs.Length, inputs, INPUT.Size);
        User32.SetForegroundWindow(hwnd);
        bool ok = User32.GetForegroundWindow() == hwnd;
        if (!ok)
        {
            User32.FlashWindow(hwnd, true);
            Logger.Debug("Foreground activation failed");
        }
        return ok;
    }

    /// <summary>Send a key chord (e.g. Ctrl+L) tagged with our marker.</summary>
    public static void SendChord(params ushort[] keys)
    {
        var list = new List<INPUT>(keys.Length * 2);
        foreach (var k in keys) list.Add(User32.KeyInput(k, false, InjectedMarker));
        for (int i = keys.Length - 1; i >= 0; i--) list.Add(User32.KeyInput(keys[i], true, InjectedMarker));
        User32.SendInput((uint)list.Count, list.ToArray(), INPUT.Size);
    }

    public static async Task<bool> WaitModifiersReleasedAsync(int timeoutMs)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (User32.AnyModifierDown())
        {
            if (Environment.TickCount64 > deadline) return false;
            await Task.Delay(10).ConfigureAwait(false);
        }
        return true;
    }
}

using System.Runtime.InteropServices;
using Hopkey.App.Infrastructure;
using Hopkey.Core.Native;
using Hopkey.Core.Util;

namespace Hopkey.App.QuickSwitch;

public enum NavigateResult { Ok, NotFound, Blocked, Timeout, Failed }

/// <summary>
/// Navigates a common file dialog to a folder.
/// Primary (no focus change, no synthetic input, ~10–50ms): save filename text → WM_SETTEXT folder into the filename
/// edit → verify → post Enter (fallback BM_CLICK on IDOK) → wait for the address to change → restore filename.
/// Fallback: Ctrl+L → wait for the address edit to get focus → WM_SETTEXT → Enter.
/// Folder pickers always use the fallback (Enter in their edit would confirm the dialog).
/// </summary>
public sealed class FileDialogNavigator
{
    private const uint Timeout = 500;

    public async Task<NavigateResult> NavigateAsync(FileDialogInfo dlg, string folder, CancellationToken ct = default)
    {
        var start = PerfLog.Start();
        try
        {
            folder = PathUtil.TrimEndSeparator(folder);
            bool shellPath = folder.StartsWith("shell:", StringComparison.OrdinalIgnoreCase) || folder.StartsWith("::", StringComparison.Ordinal);
            if (!shellPath && !await PathUtil.DirectoryExistsAsync(folder, TimeSpan.FromSeconds(1)).ConfigureAwait(false))
                return NavigateResult.NotFound;
            if (dlg.TargetElevated) return NavigateResult.Blocked;
            if (!User32.IsWindow(dlg.Hwnd)) return NavigateResult.Failed;

            // Refresh child handles (dialogs can recreate controls, e.g. after switching views).
            RefreshHandles(dlg);

            // Already there: the address never changes, so waiting for it would only end in a 2 s timeout + error flash.
            var current = shellPath ? null : TryGetCurrentFolder(dlg);
            Logger.Debug($"Navigate {folder}: dialog shows '{ReadAddress(dlg)}' → current '{current}'");
            if (PathUtil.Equal(current, folder)) return NavigateResult.Ok;

            if (dlg.IsFolderPicker || dlg.FileNameEdit == IntPtr.Zero || !User32.IsWindow(dlg.FileNameEdit))
                return await ViaAddressBarAsync(dlg, folder, ct).ConfigureAwait(false);

            var r = await ViaFileNameEditAsync(dlg, folder, ct).ConfigureAwait(false);
            if (r == NavigateResult.Ok) return r;
            return await ViaAddressBarAsync(dlg, folder, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return NavigateResult.Timeout; }
        catch (Exception ex)
        {
            Logger.Warn($"Dialog navigation failed: {dlg} → {folder}", ex);
            return NavigateResult.Failed;
        }
        finally { PerfLog.Stop("quickswitch.navigate", start); }
    }

    /// <summary>Navigate to the file's folder and put its name into the filename box (no confirmation).</summary>
    public async Task<NavigateResult> NavigateToFileAsync(FileDialogInfo dlg, string filePath, CancellationToken ct = default)
    {
        var parent = PathUtil.GetParent(filePath);
        if (parent == null) return NavigateResult.Failed;
        var r = await NavigateAsync(dlg, parent, ct).ConfigureAwait(false);
        if (r != NavigateResult.Ok) return r;
        if (dlg.FileNameEdit != IntPtr.Zero && !dlg.IsFolderPicker)
        {
            User32.SetControlText(dlg.FileNameEdit, Path.GetFileName(filePath), Timeout);
            User32.SendMessageSafe(dlg.FileNameEdit, User32.EM_SETSEL, IntPtr.Zero, new IntPtr(-1), Timeout);
        }
        return NavigateResult.Ok;
    }

    public string? TryGetCurrentFolder(FileDialogInfo dlg)
    {
        var addr = ReadAddress(dlg);
        return PathUtil.IsFileSystemPath(addr) ? addr : null;
    }

    private static void RefreshHandles(FileDialogInfo dlg)
    {
        if (dlg.AddressToolbar == IntPtr.Zero || !User32.IsWindow(dlg.AddressToolbar))
            dlg.AddressToolbar = FileDialogDetector.FindAddressToolbar(dlg.Hwnd);
        if (dlg.FileNameEdit != IntPtr.Zero && !User32.IsWindow(dlg.FileNameEdit) && dlg.IsSaveDialog)
            dlg.FileNameEdit = FileDialogDetector.FindSaveEdit(dlg.Hwnd);
    }

    /// <summary>Address toolbar text with the localized "Address: " prefix removed.</summary>
    public static string? ReadAddress(FileDialogInfo dlg)
    {
        if (dlg.AddressToolbar == IntPtr.Zero || !User32.IsWindow(dlg.AddressToolbar))
            dlg.AddressToolbar = FileDialogDetector.FindAddressToolbar(dlg.Hwnd);
        if (dlg.AddressToolbar == IntPtr.Zero) return null;
        var text = User32.GetControlText(dlg.AddressToolbar, Timeout);
        if (string.IsNullOrEmpty(text)) return text;
        int idx = text.IndexOf(": ", StringComparison.Ordinal);
        int idx2 = text.IndexOf('：');
        if (idx2 >= 0 && (idx < 0 || idx2 < idx)) return text[(idx2 + 1)..].Trim();
        if (idx >= 0) return text[(idx + 2)..].Trim();
        return text.Trim();
    }

    private static string NormalizeForDialog(string folder) =>
        folder.Length == 2 && folder[1] == ':' ? folder + "\\" : folder;

    private async Task<NavigateResult> ViaFileNameEditAsync(FileDialogInfo dlg, string folder, CancellationToken ct)
    {
        var edit = dlg.FileNameEdit;
        var target = NormalizeForDialog(folder);
        string? before = ReadAddress(dlg);
        string? orig = User32.GetControlText(edit, Timeout);
        if (orig == null) return NavigateResult.Failed; // hung / blocked

        if (!User32.SetControlText(edit, target, Timeout) || User32.GetControlText(edit, Timeout) != target)
        {
            User32.SetControlText(edit, orig, Timeout);
            return NavigateResult.Failed; // never press Enter if the text didn't take
        }

        User32.PostMessage(edit, User32.WM_KEYDOWN, new IntPtr(User32.VK_RETURN), new IntPtr(0x001C0001));
        User32.PostMessage(edit, User32.WM_KEYUP, new IntPtr(User32.VK_RETURN), unchecked(new IntPtr((int)0xC01C0001)));

        bool changed = await PollAsync(10, 300, () => Changed(dlg, before, edit, target), ct).ConfigureAwait(false);
        if (!changed && User32.IsWindow(dlg.Hwnd) && dlg.OkButton != IntPtr.Zero)
        {
            if (User32.GetControlText(edit, Timeout) != target) User32.SetControlText(edit, target, Timeout);
            User32.SendMessageSafe(dlg.OkButton, User32.BM_CLICK, IntPtr.Zero, IntPtr.Zero, Timeout);
            changed = await PollAsync(10, 700, () => Changed(dlg, before, edit, target), ct).ConfigureAwait(false);
        }

        if (User32.IsWindow(edit))
        {
            User32.SetControlText(edit, orig, Timeout);
            int selEnd = orig.LastIndexOf('.') is int dot && dot > 0 ? dot : orig.Length;
            User32.SendMessageSafe(edit, User32.EM_SETSEL, IntPtr.Zero, new IntPtr(selEnd), Timeout);
        }
        return changed ? NavigateResult.Ok : NavigateResult.Failed;
    }

    private static bool Changed(FileDialogInfo dlg, string? before, IntPtr edit, string target)
    {
        if (!User32.IsWindow(dlg.Hwnd)) return true;
        var now = ReadAddress(dlg);
        if (now != null && before != null && now != before) return true;
        var t = User32.GetControlText(edit, 200);
        return t != null && t != target;
    }

    private async Task<NavigateResult> ViaAddressBarAsync(FileDialogInfo dlg, string folder, CancellationToken ct)
    {
        await ForegroundHelper.WaitModifiersReleasedAsync(500).ConfigureAwait(false);
        string? before = ReadAddress(dlg);
        ForegroundHelper.Activate(dlg.Hwnd);
        await PollAsync(5, 300, () => User32.GetForegroundWindow() == dlg.Hwnd, ct).ConfigureAwait(false);
        ForegroundHelper.SendChord(User32.VK_CONTROL, (ushort)'L');

        IntPtr addr = IntPtr.Zero;
        bool focused = await PollAsync(10, 1000, () =>
        {
            var gti = new GUITHREADINFO { cbSize = Marshal.SizeOf<GUITHREADINFO>() };
            if (!User32.GetGUIThreadInfo(dlg.ThreadId, ref gti) || gti.hwndFocus == IntPtr.Zero) return false;
            if (!User32.ClassNameEquals(gti.hwndFocus, "Edit")) return false;
            var band = User32.FindDescendant(dlg.Hwnd, "Address Band Root");
            if (band == IntPtr.Zero) return false;
            for (var p = User32.GetParent(gti.hwndFocus); p != IntPtr.Zero; p = User32.GetParent(p))
            {
                if (p == band) { addr = gti.hwndFocus; return true; }
                if (p == dlg.Hwnd) break;
            }
            return false;
        }, ct).ConfigureAwait(false);
        if (!focused || addr == IntPtr.Zero) return NavigateResult.Timeout;

        User32.SetControlText(addr, NormalizeForDialog(folder), Timeout);
        User32.PostMessage(addr, User32.WM_KEYDOWN, new IntPtr(User32.VK_RETURN), new IntPtr(0x001C0001));
        User32.PostMessage(addr, User32.WM_KEYUP, new IntPtr(User32.VK_RETURN), unchecked(new IntPtr((int)0xC01C0001)));
        bool changed = await PollAsync(10, 1000, () =>
        {
            var now = ReadAddress(dlg);
            return now != null && now != before;
        }, ct).ConfigureAwait(false);

        // Put focus back on the filename box.
        if (dlg.FileNameEdit != IntPtr.Zero && User32.IsWindow(dlg.FileNameEdit))
        {
            uint me = Kernel32.GetCurrentThreadId();
            if (User32.AttachThreadInput(me, dlg.ThreadId, true))
            {
                User32.SetFocus(dlg.FileNameEdit);
                User32.AttachThreadInput(me, dlg.ThreadId, false);
            }
        }
        return changed || PathUtil.Equal(ReadAddress(dlg), folder) ? NavigateResult.Ok : NavigateResult.Failed;
    }

    private static async Task<bool> PollAsync(int intervalMs, int timeoutMs, Func<bool> condition, CancellationToken ct)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (true)
        {
            if (condition()) return true;
            if (Environment.TickCount64 >= deadline) return false;
            await Task.Delay(intervalMs, ct).ConfigureAwait(false);
        }
    }
}

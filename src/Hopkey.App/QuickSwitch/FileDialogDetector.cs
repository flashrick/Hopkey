using Hopkey.App.Infrastructure;
using Hopkey.Core.Native;

namespace Hopkey.App.QuickSwitch;

public enum FileDialogStyle { Modern, Legacy }

/// <summary>Snapshot of a common file dialog's important child windows.</summary>
public sealed class FileDialogInfo
{
    public IntPtr Hwnd;
    public IntPtr FileNameEdit;
    public IntPtr OkButton;
    public IntPtr AddressToolbar;
    public IntPtr AddressEdit; // may be zero until the address bar is opened
    public FileDialogStyle Style;
    public bool IsFolderPicker;
    public bool IsSaveDialog;
    public uint ProcessId;
    public uint ThreadId;
    public bool TargetElevated;
    public string ProcessName = "";

    public override string ToString() => $"Dialog 0x{Hwnd:X} {Style} save={IsSaveDialog} folder={IsFolderPicker} proc={ProcessName}";
}

/// <summary>
/// Detects standard common file dialogs (#32770). Modern Open: ComboBoxEx32 id 0x047C; Save/folder picker: DUIViewWndClassName
/// child; legacy heuristic: Button + Edit with (ToolbarWindow32 + DirectUIHWND) or (SysListView32 + SysHeader32).
/// All local read-only window queries (&lt;1ms).
/// </summary>
public static class FileDialogDetector
{
    private const int IdFileNameCombo = 0x047C; // cmb13
    private const int IdSaveEdit = 0x03E9;      // 1001
    private const int IdLegacyEdit = 0x0480;    // edt1 (1152)
    private const int IdFileTypeCombo = 0x0470; // cmb1
    private const int IdAddressEdit = 0xA205;

    public static bool IsDialogClass(IntPtr hwnd) => User32.ClassNameEquals(hwnd, "#32770");

    public static bool TryDetect(IntPtr hwnd, IReadOnlyCollection<string> disabledProcesses, out FileDialogInfo info)
    {
        info = null!;
        if (hwnd == IntPtr.Zero || !IsDialogClass(hwnd) || !User32.IsWindowVisible(hwnd)) return false;
        var wi = WindowInfoCache.Get(hwnd);
        if (disabledProcesses.Count > 0 && disabledProcesses.Any(p => string.Equals(p, wi.ProcessName, StringComparison.OrdinalIgnoreCase)))
            return false;

        var d = new FileDialogInfo { Hwnd = hwnd, ProcessId = wi.ProcessId, ThreadId = wi.ThreadId, ProcessName = wi.ProcessName, TargetElevated = wi.Elevated };

        IntPtr combo = User32.GetDlgItem(hwnd, IdFileNameCombo);
        if (combo != IntPtr.Zero && User32.ClassNameEquals(combo, "ComboBoxEx32"))
        {
            // Open dialog: ComboBoxEx32 → ComboBox → Edit
            IntPtr inner = User32.GetDlgItem(combo, IdFileNameCombo);
            IntPtr edit = inner != IntPtr.Zero ? User32.GetDlgItem(inner, IdFileNameCombo) : IntPtr.Zero;
            if (edit == IntPtr.Zero) edit = User32.FindDescendant(combo, "Edit");
            d.FileNameEdit = edit;
            d.Style = FileDialogStyle.Modern;
        }
        else if (User32.FindWindowEx(hwnd, IntPtr.Zero, "DUIViewWndClassName", null) != IntPtr.Zero)
        {
            // Save dialog / folder picker: DirectUIHWND → FloatNotifySink → ComboBox → Edit(1001)
            d.Style = FileDialogStyle.Modern;
            d.IsSaveDialog = true;
            d.FileNameEdit = FindSaveEdit(hwnd);
            // Folder pickers (FOS_PICKFOLDERS) have no "Save as type" combo. When unsure we treat it as a folder picker,
            // because pressing Enter in a folder picker's edit would confirm and close it (address-bar path is safe).
            d.IsFolderPicker = User32.GetDlgItem(hwnd, IdFileTypeCombo) == IntPtr.Zero && !HasFileTypeCombo(hwnd);
            if (d.IsFolderPicker) d.IsSaveDialog = false;
        }
        else
        {
            bool hasButton = User32.FindWindowEx(hwnd, IntPtr.Zero, "Button", null) != IntPtr.Zero;
            IntPtr edit = User32.GetDlgItem(hwnd, IdLegacyEdit);
            if (edit == IntPtr.Zero || !User32.ClassNameEquals(edit, "Edit")) edit = User32.FindDescendant(hwnd, "Edit");
            bool shellView = (User32.HasDescendant(hwnd, "ToolbarWindow32") && User32.HasDescendant(hwnd, "DirectUIHWND"))
                             || (User32.HasDescendant(hwnd, "SysListView32") && User32.HasDescendant(hwnd, "SysHeader32"));
            if (!hasButton || edit == IntPtr.Zero || !shellView) return false;
            d.Style = FileDialogStyle.Legacy;
            d.FileNameEdit = edit;
        }

        d.OkButton = User32.GetDlgItem(hwnd, 1);
        d.AddressToolbar = FindAddressToolbar(hwnd);
        d.AddressEdit = FindAddressEdit(hwnd);
        info = d;
        return true;
    }

    private static bool HasFileTypeCombo(IntPtr hwnd)
    {
        // In modern Save dialogs the "Save as type" (and e.g. Notepad's "Encoding") combos are drop-down lists hosted in
        // DirectUIHWND/FloatNotifySink with control id 0 — i.e. ComboBoxes WITHOUT an Edit child. The filename combo has an
        // Edit (id 0x3E9). Folder pickers (FOS_PICKFOLDERS) only have the filename/"Folder:" combo.
        foreach (var sink in User32.FindDescendants(hwnd, "FloatNotifySink"))
        {
            var cb = User32.FindWindowEx(sink, IntPtr.Zero, "ComboBox", null);
            while (cb != IntPtr.Zero)
            {
                if (User32.GetDlgCtrlID(cb) is 0x0470 or 0x0471) return true;
                if (User32.FindWindowEx(cb, IntPtr.Zero, "Edit", null) == IntPtr.Zero) return true;
                cb = User32.FindWindowEx(sink, cb, "ComboBox", null);
            }
        }
        return false;
    }

    public static IntPtr FindSaveEdit(IntPtr hwnd)
    {
        foreach (var sink in User32.FindDescendants(hwnd, "FloatNotifySink"))
        {
            var cb = User32.FindWindowEx(sink, IntPtr.Zero, "ComboBox", null);
            while (cb != IntPtr.Zero)
            {
                var edit = User32.GetDlgItem(cb, IdSaveEdit);
                if (edit != IntPtr.Zero) return edit;
                var e2 = User32.FindWindowEx(cb, IntPtr.Zero, "Edit", null);
                if (e2 != IntPtr.Zero && User32.GetDlgCtrlID(cb) == IdSaveEdit) return e2;
                cb = User32.FindWindowEx(sink, cb, "ComboBox", null);
            }
        }
        return IntPtr.Zero;
    }

    public static IntPtr FindAddressToolbar(IntPtr hwnd)
    {
        var bc = User32.FindDescendant(hwnd, "Breadcrumb Parent");
        return bc != IntPtr.Zero ? User32.FindDescendant(bc, "ToolbarWindow32") : IntPtr.Zero;
    }

    public static IntPtr FindAddressEdit(IntPtr hwnd)
    {
        var band = User32.FindDescendant(hwnd, "Address Band Root");
        if (band == IntPtr.Zero) return IntPtr.Zero;
        var cbx = User32.FindDescendant(band, "ComboBoxEx32");
        if (cbx == IntPtr.Zero) return IntPtr.Zero;
        var a = User32.GetDlgItem(cbx, IdAddressEdit);
        var b = a != IntPtr.Zero ? User32.GetDlgItem(a, IdAddressEdit) : IntPtr.Zero;
        return b != IntPtr.Zero ? b : User32.FindDescendant(cbx, "Edit");
    }
}

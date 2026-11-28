using System.Runtime.InteropServices;

namespace Hopkey.Core.Native;

#pragma warning disable SYSLIB1054
public static class Shell32
{
    public const uint SHGFI_ICON = 0x000000100;
    public const uint SHGFI_SMALLICON = 0x000000001;
    public const uint SHGFI_LARGEICON = 0x000000000;
    public const uint SHGFI_USEFILEATTRIBUTES = 0x000000010;
    public const uint SHGFI_SYSICONINDEX = 0x000004000;
    public const uint FILE_ATTRIBUTE_DIRECTORY = 0x10;
    public const uint FILE_ATTRIBUTE_NORMAL = 0x80;

    public const uint SEE_MASK_INVOKEIDLIST = 0x0000000C;
    public const uint SEE_MASK_NOCLOSEPROCESS = 0x00000040;
    public const uint SEE_MASK_FLAG_NO_UI = 0x00000400;
    public const uint SEE_MASK_NOASYNC = 0x00000100;

    public const uint SBSP_SAMEBROWSER = 0x00000001;
    public const uint SBSP_ABSOLUTE = 0x00000000;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool ShellExecuteEx(ref SHELLEXECUTEINFO lpExecInfo);

    [DllImport("shell32.dll")]
    public static extern int SHOpenFolderAndSelectItems(IntPtr pidlFolder, uint cidl, IntPtr[]? apidl, uint dwFlags);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr ILCreateFromPath(string pszPath);

    [DllImport("shell32.dll")]
    public static extern void ILFree(IntPtr pidl);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern int SHParseDisplayName(string name, IntPtr bindingContext, out IntPtr pidl, uint sfgaoIn, out uint psfgaoOut);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    public static extern void SHCreateItemFromParsingName(string path, IntPtr pbc, [In] ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern bool SHGetPathFromIDListW(IntPtr pidl, [Out] char[] pszPath);

    public static bool ShellExecute(string file, string? args = null, string? verb = null, string? dir = null, IntPtr owner = default, bool invokeIdList = false, bool hidden = false)
    {
        var sei = new SHELLEXECUTEINFO
        {
            cbSize = Marshal.SizeOf<SHELLEXECUTEINFO>(),
            fMask = (invokeIdList ? SEE_MASK_INVOKEIDLIST : 0) | SEE_MASK_NOASYNC,
            hwnd = owner,
            lpVerb = verb,
            lpFile = file,
            lpParameters = args,
            lpDirectory = dir,
            nShow = hidden ? User32.SW_HIDE : User32.SW_SHOWNORMAL,
        };
        if (!ShellExecuteEx(ref sei))
        {
            int err = Marshal.GetLastWin32Error();
            if (err == Kernel32.ERROR_CANCELLED) return false;
            throw new System.ComponentModel.Win32Exception(err);
        }
        return true;
    }

    /// <summary>Open Explorer at the parent folder with the item selected.</summary>
    public static bool OpenFolderAndSelect(string path)
    {
        IntPtr pidl = ILCreateFromPath(path);
        if (pidl == IntPtr.Zero) return false;
        try { return SHOpenFolderAndSelectItems(pidl, 0, null, 0) == 0; }
        finally { ILFree(pidl); }
    }
}

public static class Dwmapi
{
    public const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    public const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    public const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    public const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
    public const int DWMWCP_ROUND = 2;
    public const int DWMWCP_ROUNDSMALL = 3;

    [DllImport("dwmapi.dll")]
    public static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out RECT pvAttribute, int cbAttribute);

    [DllImport("dwmapi.dll")]
    public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    public static RECT GetExtendedFrameBounds(IntPtr hwnd)
    {
        if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out var r, Marshal.SizeOf<RECT>()) == 0 && r.Width > 0)
            return r;
        User32.GetWindowRect(hwnd, out r);
        return r;
    }

    public static void SetRoundCorners(IntPtr hwnd, bool small = false)
    {
        int v = small ? DWMWCP_ROUNDSMALL : DWMWCP_ROUND;
        DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref v, sizeof(int));
    }

    public static void SetDarkMode(IntPtr hwnd, bool dark)
    {
        int v = dark ? 1 : 0;
        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref v, sizeof(int));
    }

    public const int DWMWA_NCRENDERING_POLICY = 2;
    public const int DWMWA_BORDER_COLOR = 34;   // Win11 22000+
    public const int DWMWA_CAPTION_COLOR = 35;
    public const int DWMWA_TEXT_COLOR = 36;

    [StructLayout(LayoutKind.Sequential)]
    public struct MARGINS { public int Left, Right, Top, Bottom; }

    [DllImport("dwmapi.dll")]
    public static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS margins);

    /// <summary>Sets a DWM color attribute. colorRef is 0x00BBGGRR.</summary>
    public static void SetColor(IntPtr hwnd, int attr, uint colorRef)
    {
        int v = unchecked((int)colorRef);
        DwmSetWindowAttribute(hwnd, attr, ref v, sizeof(int));
    }

    /// <summary>Borderless windows: ask DWM to draw its drop shadow. Call once after the HWND exists.</summary>
    public static void EnableShadow(IntPtr hwnd)
    {
        int policy = 2; // DWMNCRP_ENABLED
        DwmSetWindowAttribute(hwnd, DWMWA_NCRENDERING_POLICY, ref policy, sizeof(int));
        var m = new MARGINS { Bottom = 1 }; // 1px hidden under WPF's opaque content
        DwmExtendFrameIntoClientArea(hwnd, ref m);
    }

    public static bool IsWin11 => Environment.OSVersion.Version.Build >= 22000;
}

public static class Gdi32
{
    [DllImport("gdi32.dll")]
    public static extern bool DeleteObject(IntPtr hObject);

    [DllImport("gdi32.dll")]
    public static extern int GetObject(IntPtr hgdiobj, int cbBuffer, out BITMAP lpvObject);

    [DllImport("gdi32.dll", EntryPoint = "GetObject")]
    public static extern int GetObjectDib(IntPtr hgdiobj, int cbBuffer, out DIBSECTION lpvObject);
}

public static class Advapi32
{
    public const uint TOKEN_QUERY = 0x0008;
    public const int TokenElevation = 20;

    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern bool GetTokenInformation(IntPtr tokenHandle, int tokenInformationClass, out TOKEN_ELEVATION tokenInformation, int tokenInformationLength, out int returnLength);
}
#pragma warning restore SYSLIB1054

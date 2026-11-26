using System.Runtime.InteropServices;
using System.Security.Principal;

namespace Hopkey.Core.Native;

public static class ElevationHelper
{
    private static readonly Lazy<bool> s_selfElevated = new(() =>
    {
        using var id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    });

    public static bool IsSelfElevated => s_selfElevated.Value;

    /// <summary>True if the target process is elevated. If its token can't be opened we assume elevated (UIPI will block us anyway).</summary>
    public static bool IsProcessElevated(uint pid)
    {
        if (pid == 0) return false;
        IntPtr hProc = Kernel32.OpenProcess(Kernel32.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (hProc == IntPtr.Zero) return true;
        try
        {
            if (!Advapi32.OpenProcessToken(hProc, Advapi32.TOKEN_QUERY, out var hTok)) return true;
            try
            {
                return Advapi32.GetTokenInformation(hTok, Advapi32.TokenElevation, out var te, Marshal.SizeOf<TOKEN_ELEVATION>(), out _)
                       && te.TokenIsElevated != 0;
            }
            finally { Kernel32.CloseHandle(hTok); }
        }
        finally { Kernel32.CloseHandle(hProc); }
    }

    /// <summary>True when we cannot send input/messages to the target because of UIPI.</summary>
    public static bool IsBlockedByUipi(uint pid) => !IsSelfElevated && IsProcessElevated(pid);
}

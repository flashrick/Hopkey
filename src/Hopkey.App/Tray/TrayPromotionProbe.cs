using Hopkey.Core.Config;
using Hopkey.Core.Native;
using Microsoft.Win32;

namespace Hopkey.App.Tray;

/// <summary>
/// Reads Windows 11's per-icon "show in taskbar" flag (HKCU\Control Panel\NotifyIconSettings\*\IsPromoted).
/// Undocumented: Win10, a missing key or any error → Unknown, and the UI falls back to generic instructions.
/// </summary>
internal static class TrayPromotionProbe
{
    private static readonly (string Guid, Environment.SpecialFolder Folder)[] s_knownFolders =
    {
        ("{6D809377-6AF0-444B-8957-A3773F02200E}", Environment.SpecialFolder.ProgramFiles),
        ("{7C5A40EF-A0FB-4BFC-874A-C0F2E0B9FA8E}", Environment.SpecialFolder.ProgramFilesX86),
        ("{F38BF404-1D43-42F2-9305-67DE0B28FC23}", Environment.SpecialFolder.Windows),
        ("{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}", Environment.SpecialFolder.System),
    };

    public static TrayPromotion Read()
    {
        if (!Dwmapi.IsWin11) return TrayPromotion.Unknown;
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(@"Control Panel\NotifyIconSettings");
            if (root == null) return TrayPromotion.Unknown;
            var entries = new List<(string, bool)>();
            foreach (var name in root.GetSubKeyNames())
            {
                using var k = root.OpenSubKey(name);
                if (k?.GetValue("ExecutablePath") is not string p) continue;
                entries.Add((ExpandKnownFolder(p), k.GetValue("IsPromoted") is int v && v == 1));
            }
            return OnboardingPolicy.EvaluatePromotion(entries, AppPaths.ExecutablePath);
        }
        catch { return TrayPromotion.Unknown; }
    }

    // Explorer stores some paths as "{KNOWNFOLDERID}\rest"; unknown GUIDs stay as-is (file-name fallback handles them).
    private static string ExpandKnownFolder(string p)
    {
        if (!p.StartsWith('{')) return p;
        foreach (var (guid, folder) in s_knownFolders)
            if (p.StartsWith(guid, StringComparison.OrdinalIgnoreCase))
                return Environment.GetFolderPath(folder) + p[guid.Length..];
        return p;
    }
}

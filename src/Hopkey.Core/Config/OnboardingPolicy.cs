using System.Globalization;

namespace Hopkey.Core.Config;

public enum TrayPromotion { Unknown, Hidden, Promoted }

/// <summary>Pure rules for the first-run guide (unit-tested in Hopkey.SelfTest).</summary>
public static class OnboardingPolicy
{
    public static bool IsNewUser(bool settingsExisted, bool historyExisted) => !settingsExisted && !historyExisted;

    /// <param name="autostart">--autostart (login): never pop a window at login.</param>
    /// <param name="elevatedRestart">--elevated-restart: the user is mid-task; settings opens instead.</param>
    /// <param name="forced">--onboarding (manual / testing).</param>
    public static bool ShouldAutoShow(OnboardingState s, bool autostart, bool elevatedRestart, bool forced)
    {
        if (forced) return true;
        if (autostart || elevatedRestart) return false;
        return s.SeenVersion < OnboardingState.CurrentVersion;
    }

    /// <summary>UI language for a brand-new user: Chinese for zh-* systems, otherwise English.</summary>
    public static string DefaultLanguage(CultureInfo ui) =>
        ui.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? "zh-CN" : "en";

    /// <summary>Keycaps for a hotkey spec: DoubleCtrl → [Ctrl, Ctrl]; "Ctrl+Alt+Space" → [Ctrl, Alt, Space]; "" → [].</summary>
    public static IReadOnlyList<string> Keycaps(string? spec)
    {
        if (string.IsNullOrWhiteSpace(spec)) return Array.Empty<string>();
        if (HotkeySpec.Parse(spec).IsDoubleCtrl) return new[] { "Ctrl", "Ctrl" };
        return spec.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    /// <summary>
    /// Win11 HKCU\Control Panel\NotifyIconSettings\* entries → is our icon pinned to the taskbar?
    /// Full-path match first; else a unique file-name match; else Unknown.
    /// </summary>
    public static TrayPromotion EvaluatePromotion(IEnumerable<(string ExePath, bool Promoted)> entries, string selfExePath)
    {
        var list = entries.ToList();
        var match = list.Where(e => string.Equals(e.ExePath, selfExePath, StringComparison.OrdinalIgnoreCase)).ToList();
        if (match.Count == 0)
        {
            var name = Path.GetFileName(selfExePath);
            match = list.Where(e => string.Equals(Path.GetFileName(e.ExePath), name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (match.Count != 1) return TrayPromotion.Unknown;
        }
        return match.Any(e => e.Promoted) ? TrayPromotion.Promoted : TrayPromotion.Hidden;
    }
}

using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Hopkey.Core.Config;
using Hopkey.Core.Native;
using Microsoft.Win32;
using ThemeMode = Hopkey.Core.Config.ThemeMode;

namespace Hopkey.App.Infrastructure;

/// <summary>Light/Dark/System theme. Brushes are built in code (frozen) so any UI thread can create its own copy.</summary>
public static class ThemeManager
{
    private const string Marker = "CL.Theme";
    public static bool IsDark { get; private set; }
    public static event Action? ThemeChanged;
    private static ThemeMode s_mode = ThemeMode.System;

    public static void Initialize(ThemeMode mode)
    {
        s_mode = mode;
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category == UserPreferenceCategory.General && s_mode == ThemeMode.System)
                Application.Current?.Dispatcher.BeginInvoke(() => Apply(s_mode));
        };
        Apply(mode);
    }

    public static bool SystemUsesDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch { return false; }
    }

    public static void Apply(ThemeMode mode)
    {
        s_mode = mode;
        IsDark = mode == ThemeMode.Dark || (mode == ThemeMode.System && SystemUsesDark());
        var app = Application.Current;
        if (app == null) return;
        ReplaceDictionary(app.Resources, CreateDictionary(IsDark));
        foreach (Window w in app.Windows) ApplyWindowChrome(w);
        ThemeChanged?.Invoke();
    }

    public static void ReplaceDictionary(ResourceDictionary target, ResourceDictionary dict)
    {
        var md = target.MergedDictionaries;
        for (int i = md.Count - 1; i >= 0; i--)
            if (md[i].Contains(Marker)) md.RemoveAt(i);
        md.Insert(0, dict);
    }

    // DWM colors, 0x00BBGGRR
    public static uint PopupBorderColor => IsDark ? 0x005F3134u : 0x00ECD1D4u;   // #34315F / #D4D1EC
    private static uint CaptionColor => IsDark ? 0x001A0C0Du : 0x00FAF2F3u;      // #0D0C1A / #F3F2FA
    private static uint CaptionTextColor => IsDark ? 0x00FFEBECu : 0x002B1617u;  // #ECEBFF / #17162B
    private static uint FrameBorderColor => IsDark ? 0x004F2A2Cu : 0x00F0DEE0u;  // #2C2A4F / #E0DEF0

    public static void ApplyWindowChrome(Window w)
    {
        var hwnd = new WindowInteropHelper(w).Handle;
        if (hwnd == IntPtr.Zero) return;
        ApplyChrome(hwnd, w.WindowStyle == WindowStyle.None);
    }

    /// <summary>Dark mode + brand border (borderless) or caption colors (framed). Safe from any thread.</summary>
    public static void ApplyChrome(IntPtr hwnd, bool borderless)
    {
        Dwmapi.SetDarkMode(hwnd, IsDark);
        if (borderless)
            Dwmapi.SetColor(hwnd, Dwmapi.DWMWA_BORDER_COLOR, PopupBorderColor);
        else
        {
            Dwmapi.SetColor(hwnd, Dwmapi.DWMWA_CAPTION_COLOR, CaptionColor);
            Dwmapi.SetColor(hwnd, Dwmapi.DWMWA_TEXT_COLOR, CaptionTextColor);
            Dwmapi.SetColor(hwnd, Dwmapi.DWMWA_BORDER_COLOR, FrameBorderColor);
        }
    }

    private static Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    private static SolidColorBrush B(string hex)
    {
        var b = new SolidColorBrush(C(hex));
        b.Freeze();
        return b;
    }

    private static LinearGradientBrush LG(bool vertical, params (double Offset, string Hex)[] stops)
    {
        var b = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = vertical ? new Point(0, 1) : new Point(1, 0) };
        foreach (var (o, h) in stops) b.GradientStops.Add(new GradientStop(C(h), o));
        b.Freeze();
        return b;
    }

    private static RadialGradientBrush Aurora(string from, string to)
    {
        var b = new RadialGradientBrush(C(from), C(to))
        {
            Center = new Point(0.12, 0), GradientOrigin = new Point(0.12, 0), RadiusX = 0.6, RadiusY = 1.3,
        };
        b.Freeze();
        return b;
    }

    private static FontFamily F(string families) => new(families);

    public static ResourceDictionary CreateDictionary(bool dark)
    {
        var d = new ResourceDictionary { [Marker] = true };
        if (dark)
        {
            d["Brush.Background"] = B("#0D0C1A");
            d["Brush.Card"] = B("#17162C");
            d["Brush.CardAlt"] = B("#1D1C36");
            d["Brush.Selected"] = B("#2A2766");
            d["Brush.Hover"] = B("#211F40");
            d["Brush.Accent"] = B("#A5B4FC");
            d["Brush.AccentText"] = B("#FFFFFF");
            d["Brush.Text"] = B("#ECEBFF");
            d["Brush.SubText"] = B("#9D9BC7");
            d["Brush.Border"] = B("#2C2A4F");
            d["Brush.Input"] = B("#1B1A33");
            d["Brush.Error"] = B("#FF7A8A");
            d["Brush.Warning"] = B("#FBBF24");
            d["Brush.Highlight"] = B("#C7D2FE");

            d["Brush.Surface"] = B("#121124");
            d["Brush.Footer"] = B("#121124");
            d["Brush.Divider"] = B("#232142");
            d["Brush.TextFaint"] = B("#7C7AA8");
            d["Brush.Plate"] = B("#1E1D3A");
            d["Brush.PlateEdge"] = B("#34315F");
            d["Brush.KeyFace"] = B("#232144");
            d["Brush.KeyEdge"] = B("#3A3768");
            d["Brush.KeyText"] = B("#C9C7EE");
            d["Brush.AccentFill"] = B("#5558E8");
            d["Brush.AccentFillHover"] = B("#6366F1");
            d["Brush.AccentFillEdge"] = B("#3B37B5");
            d["Brush.AccentSoft"] = B("#3D6366F1");
            d["Brush.HighlightBg"] = B("#4D6366F1");
            d["Brush.Success"] = B("#34D399");
            d["Brush.ErrorSoft"] = B("#33FF7A8A");
            d["Brush.WarningSoft"] = B("#33FBBF24");
            d["Brush.Focus"] = B("#A5B4FC");
            d["Brush.ScrollThumb"] = B("#3A3768");
            d["Brush.ScrollThumbHover"] = B("#4C4889");
            d["Brush.ToggleTrack"] = B("#1B1A33");
            d["Brush.ToggleEdge"] = B("#5A5790");
            d["Brush.ToggleKnob"] = B("#9D9BC7");

            d["Brush.Hop"] = LG(false, (0, "#6366F1"), (0.55, "#A78BFA"), (1, "#22D3EE"));
            d["Brush.HopV"] = LG(true, (0, "#22D3EE"), (0.5, "#818CF8"), (1, "#6366F1"));
            d["Brush.WindowBg"] = LG(true, (0, "#1A1934"), (1, "#141328"));
            d["Brush.RimLight"] = LG(false, (0, "#00C7D2FE"), (0.3, "#26C7D2FE"), (0.7, "#26C7D2FE"), (1, "#00C7D2FE"));
            d["Brush.Aurora"] = Aurora("#2E6366F1", "#006366F1");
        }
        else
        {
            d["Brush.Background"] = B("#F3F2FA");
            d["Brush.Card"] = B("#FFFFFF");
            d["Brush.CardAlt"] = B("#F1F0F9");
            d["Brush.Selected"] = B("#E6E4FF");
            d["Brush.Hover"] = B("#EEEDFA");
            d["Brush.Accent"] = B("#4F46E5");
            d["Brush.AccentText"] = B("#FFFFFF");
            d["Brush.Text"] = B("#17162B");
            d["Brush.SubText"] = B("#5E5B7E");
            d["Brush.Border"] = B("#E0DEF0");
            d["Brush.Input"] = B("#FFFFFF");
            d["Brush.Error"] = B("#C42B1C");
            d["Brush.Warning"] = B("#9D5D00");
            d["Brush.Highlight"] = B("#3730A3");

            d["Brush.Surface"] = B("#F8F7FD");
            d["Brush.Footer"] = B("#F4F3FB");
            d["Brush.Divider"] = B("#E9E7F5");
            d["Brush.TextFaint"] = B("#706E94");
            d["Brush.Plate"] = B("#FFFFFF");
            d["Brush.PlateEdge"] = B("#D9D6F2");
            d["Brush.KeyFace"] = B("#FFFFFF");
            d["Brush.KeyEdge"] = B("#D4D1EC");
            d["Brush.KeyText"] = B("#4A4766");
            d["Brush.AccentFill"] = B("#4F46E5");
            d["Brush.AccentFillHover"] = B("#4338CA");
            d["Brush.AccentFillEdge"] = B("#3730A3");
            d["Brush.AccentSoft"] = B("#1A4F46E5");
            d["Brush.HighlightBg"] = B("#2E6366F1");
            d["Brush.Success"] = B("#0F7B4F");
            d["Brush.ErrorSoft"] = B("#1AC42B1C");
            d["Brush.WarningSoft"] = B("#1A9D5D00");
            d["Brush.Focus"] = B("#4F46E5");
            d["Brush.ScrollThumb"] = B("#C9C6E4");
            d["Brush.ScrollThumbHover"] = B("#A9A5D4");
            d["Brush.ToggleTrack"] = B("#FFFFFF");
            d["Brush.ToggleEdge"] = B("#8A88A8");
            d["Brush.ToggleKnob"] = B("#5E5B7E");

            d["Brush.Hop"] = LG(false, (0, "#4F46E5"), (0.55, "#8B5CF6"), (1, "#06B6D4"));
            d["Brush.HopV"] = LG(true, (0, "#06B6D4"), (0.5, "#6366F1"), (1, "#4F46E5"));
            d["Brush.WindowBg"] = LG(true, (0, "#FDFDFF"), (1, "#F6F5FD"));
            d["Brush.RimLight"] = B("#00FFFFFF");
            d["Brush.Aurora"] = Aurora("#1A6366F1", "#006366F1");
        }
        d["Font.Family"] = F("Segoe UI Variable Text, Segoe UI, Microsoft YaHei UI");
        d["Font.Display"] = F("Segoe UI Variable Display, Segoe UI, Microsoft YaHei UI");
        d["Font.Small"] = F("Segoe UI Variable Small, Segoe UI, Microsoft YaHei UI");
        d["Font.Mono"] = F("Cascadia Mono, Consolas, Microsoft YaHei UI");
        d["Font.Numeric"] = F("Bahnschrift, Segoe UI, Microsoft YaHei UI");
        d["Font.Icon"] = F("Segoe Fluent Icons, Segoe MDL2 Assets");
        return d;
    }
}

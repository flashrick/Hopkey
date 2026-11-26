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

    public static void ApplyWindowChrome(Window w)
    {
        var hwnd = new WindowInteropHelper(w).Handle;
        if (hwnd != IntPtr.Zero) Dwmapi.SetDarkMode(hwnd, IsDark);
    }

    private static SolidColorBrush B(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }

    public static ResourceDictionary CreateDictionary(bool dark)
    {
        var d = new ResourceDictionary { [Marker] = true };
        if (dark)
        {
            d["Brush.Background"] = B("#202020");
            d["Brush.Card"] = B("#2B2B2B");
            d["Brush.CardAlt"] = B("#323232");
            d["Brush.Selected"] = B("#2D3E50");
            d["Brush.Hover"] = B("#383838");
            d["Brush.Accent"] = B("#4CC2FF");
            d["Brush.AccentText"] = B("#000000");
            d["Brush.Text"] = B("#F2F2F2");
            d["Brush.SubText"] = B("#A0A0A0");
            d["Brush.Border"] = B("#3D3D3D");
            d["Brush.Input"] = B("#2B2B2B");
            d["Brush.Error"] = B("#FF6B6B");
            d["Brush.Warning"] = B("#FFB900");
            d["Brush.Highlight"] = B("#4CC2FF");
        }
        else
        {
            d["Brush.Background"] = B("#F9F9F9");
            d["Brush.Card"] = B("#FFFFFF");
            d["Brush.CardAlt"] = B("#F3F3F3");
            d["Brush.Selected"] = B("#E5F1FB");
            d["Brush.Hover"] = B("#F0F0F0");
            d["Brush.Accent"] = B("#0067C0");
            d["Brush.AccentText"] = B("#FFFFFF");
            d["Brush.Text"] = B("#1A1A1A");
            d["Brush.SubText"] = B("#5F5F5F");
            d["Brush.Border"] = B("#DADADA");
            d["Brush.Input"] = B("#FFFFFF");
            d["Brush.Error"] = B("#C42B1C");
            d["Brush.Warning"] = B("#9D5D00");
            d["Brush.Highlight"] = B("#0067C0");
        }
        d["Font.Family"] = new FontFamily("Segoe UI Variable Text, Segoe UI, Microsoft YaHei UI");
        return d;
    }
}

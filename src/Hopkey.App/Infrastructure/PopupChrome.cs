using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using Hopkey.Core.Native;

namespace Hopkey.App.Infrastructure;

/// <summary>
/// Rounds popup HWNDs (context menus, combo drop-downs, tooltips) through DWM instead of AllowsTransparency,
/// and gives them the brand border color. On Win10 these calls fail silently and the popup stays square.
/// </summary>
public static class PopupChrome
{
    public static readonly DependencyProperty RoundedProperty = DependencyProperty.RegisterAttached(
        "Rounded", typeof(bool), typeof(PopupChrome), new PropertyMetadata(false, OnChanged));

    public static void SetRounded(DependencyObject d, bool v) => d.SetValue(RoundedProperty, v);
    public static bool GetRounded(DependencyObject d) => (bool)d.GetValue(RoundedProperty);

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is not true) return;
        switch (d)
        {
            case Popup p: p.Opened += (_, _) => Apply(p.Child); break;
            case ContextMenu m: m.Opened += (_, _) => Apply(m); break;
            case ToolTip t: t.Opened += (_, _) => Apply(t); break;
        }
    }

    private static void Apply(Visual? v)
    {
        if (v == null || PresentationSource.FromVisual(v) is not HwndSource src) return;
        Dwmapi.SetRoundCorners(src.Handle);
        Dwmapi.SetColor(src.Handle, Dwmapi.DWMWA_BORDER_COLOR, ThemeManager.PopupBorderColor);
    }
}

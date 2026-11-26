using System.Windows.Media;
using Hopkey.App.Infrastructure;
using Hopkey.App.Mvvm;
using Hopkey.App.QuickSwitch;
using Hopkey.Core.Search;

namespace Hopkey.App.Launcher;

public enum LauncherContextKind { Normal, Explorer, Dialog }

/// <summary>Normal launcher, or "context mode" bound to an Explorer window / file dialog (results navigate that window).</summary>
public sealed record LauncherContext(LauncherContextKind Kind, IntPtr Hwnd, FileDialogInfo? Dialog, string Title)
{
    public static LauncherContext Normal { get; } = new(LauncherContextKind.Normal, IntPtr.Zero, null, "");
    public bool IsContext => Kind != LauncherContextKind.Normal;
}

public sealed class ResultItemViewModel : ObservableObject
{
    private ImageSource _icon;
    public SearchResult Result { get; }
    public string Title => Result.Title;
    public string? Subtitle => Result.Subtitle == null ? null : Core.Util.PathUtil.Ellipsize(Result.Subtitle, 110);
    public IReadOnlyList<(int Start, int Length)> Highlights => Result.Highlights;
    public string? Badge { get; }
    public int Index { get; }
    public string Shortcut => Index < 9 ? $"Alt+{Index + 1}" : "";

    public ImageSource Icon
    {
        get => _icon;
        set => Set(ref _icon, value);
    }

    public ResultItemViewModel(SearchResult r, int index, IconCache icons, System.Windows.Threading.Dispatcher ui)
    {
        Result = r;
        Index = index;
        Badge = r.Group != null ? LocalizationManager.T("Launcher.Group." + r.Group) : r.IsFavorite ? "★" : null;
        _icon = r.Kind switch
        {
            ResultKind.Web => Glyphs.Web,
            ResultKind.Calculator => Glyphs.Calculator,
            ResultKind.Command => Glyphs.Command,
            _ => icons.Get(r.Path, r.Kind == ResultKind.Folder, r.Kind == ResultKind.App,
                img => ui.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () => Icon = img)),
        };
    }
}

public sealed class ActionItemViewModel
{
    public required string Name { get; init; }
    public string? Shortcut { get; init; }
    public required ResultActionKind Kind { get; init; }
    public string Glyph { get; init; } = "";
}

/// <summary>Vector glyph images for non-file results (frozen, shared).</summary>
public static class Glyphs
{
    public static readonly ImageSource Web = Make("", Color.FromRgb(0x00, 0x78, 0xD4));
    public static readonly ImageSource Calculator = Make("", Color.FromRgb(0x10, 0x7C, 0x10));
    public static readonly ImageSource Command = Make("", Color.FromRgb(0x60, 0x60, 0x60));

    private static ImageSource Make(string glyph, Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        var tf = new Typeface(new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), System.Windows.FontStyles.Normal,
            System.Windows.FontWeights.Normal, System.Windows.FontStretches.Normal);
        var ft = new FormattedText(glyph, System.Globalization.CultureInfo.InvariantCulture, System.Windows.FlowDirection.LeftToRight, tf, 24, brush, 1.0);
        var g = new DrawingGroup();
        using (var dc = g.Open())
        {
            dc.DrawRectangle(Brushes.Transparent, null, new System.Windows.Rect(0, 0, 32, 32));
            dc.DrawText(ft, new System.Windows.Point((32 - ft.Width) / 2, (32 - ft.Height) / 2));
        }
        var img = new DrawingImage(g);
        img.Freeze();
        return img;
    }
}

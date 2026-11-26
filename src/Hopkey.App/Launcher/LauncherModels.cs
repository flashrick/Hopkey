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

/// <summary>Which file details the launcher shows under each file/folder result.</summary>
public readonly record struct ResultDetailOptions(bool Created, bool Modified, bool Size)
{
    public bool Any => Created || Modified || Size;
}

public sealed class ResultItemViewModel : ObservableObject
{
    private ImageSource _icon;
    private readonly ResultDetailOptions _detailOptions;
    private readonly System.Windows.Threading.Dispatcher _ui;
    private string? _details;
    private bool _detailsRequested;

    /// <summary>
    /// Date/size line for file and folder results. The index stores no metadata, so it's read from disk on first
    /// binding — only for rows the virtualized list actually realizes — on a pool thread.
    /// </summary>
    public string? Details
    {
        get
        {
            if (!_detailsRequested) { _detailsRequested = true; RequestDetails(); }
            return _details;
        }
    }

    private void RequestDetails()
    {
        var r = Result;
        if (!_detailOptions.Any || r.Path == null || r.Kind is not (ResultKind.File or ResultKind.Folder)) return;
        var opt = _detailOptions;
        string path = r.Path;
        bool folder = r.Kind == ResultKind.Folder;
        Task.Run(() =>
        {
            try
            {
                FileSystemInfo fi = folder ? new DirectoryInfo(path) : new FileInfo(path);
                if (!fi.Exists) return;
                var parts = new List<string>(3);
                if (opt.Modified) parts.Add(LocalizationManager.F("Launcher.Detail.Modified", fi.LastWriteTime.ToString("yyyy-MM-dd HH:mm")));
                if (opt.Created) parts.Add(LocalizationManager.F("Launcher.Detail.Created", fi.CreationTime.ToString("yyyy-MM-dd HH:mm")));
                if (opt.Size && fi is FileInfo f) parts.Add(FormatSize(f.Length));
                if (parts.Count == 0) return;
                var text = string.Join("  ·  ", parts);
                _ui.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () => { _details = text; OnPropertyChanged(nameof(Details)); });
            }
            catch { /* access denied / gone: show nothing */ }
        });
    }

    public static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        string[] units = { "KB", "MB", "GB", "TB" };
        double v = bytes;
        int i = -1;
        do { v /= 1024; i++; } while (v >= 1024 && i < units.Length - 1);
        return v >= 100 ? $"{v:F0} {units[i]}" : v >= 10 ? $"{v:F1} {units[i]}" : $"{v:F2} {units[i]}";
    }
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

    public ResultItemViewModel(SearchResult r, int index, IconCache icons, System.Windows.Threading.Dispatcher ui,
        ResultDetailOptions detailOptions = default)
    {
        Result = r;
        Index = index;
        _ui = ui;
        _detailOptions = detailOptions;
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

/// <summary>A type filter chip above the results ("All", "Folders", "Apps", ".pdf" …).</summary>
public sealed class FacetViewModel
{
    public required string Label { get; init; }
    public int Count { get; init; }
    /// <summary>Query token this chip adds ("folder:", "app:", "ext:pdf"); null = no filter (All).</summary>
    public string? Token { get; init; }
    public bool IsActive { get; init; }
    public string Text => Count > 0 ? $"{Label} {Count}" : Label;
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

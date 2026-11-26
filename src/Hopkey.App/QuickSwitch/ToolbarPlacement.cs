using Hopkey.Core.Native;

namespace Hopkey.App.QuickSwitch;

public enum ToolbarSide { Below, Above, Inside }

/// <summary>Toolbar placement in physical pixels: below the dialog's bottom edge, else above, else inside bottom-left.</summary>
public static class ToolbarPlacement
{
    public static (int X, int Y, ToolbarSide Side) Compute(RECT dlg, RECT work, int width, int height, int gap)
    {
        int x = dlg.Left;
        int y;
        ToolbarSide side;
        if (dlg.Bottom + gap + height <= work.Bottom)
        {
            y = dlg.Bottom + gap;
            side = ToolbarSide.Below;
        }
        else if (dlg.Top - gap - height >= work.Top)
        {
            y = dlg.Top - gap - height;
            side = ToolbarSide.Above;
        }
        else
        {
            y = dlg.Bottom - height - gap * 14; // above the button row
            x = dlg.Left + gap * 4;
            side = ToolbarSide.Inside;
        }
        x = Math.Clamp(x, work.Left, Math.Max(work.Left, work.Right - width));
        y = Math.Clamp(y, work.Top, Math.Max(work.Top, work.Bottom - height));
        return (x, y, side);
    }
}

public sealed record FolderMenuItem(string Title, string? Path, bool IsCurrent = false, bool IsDisabled = false);

/// <summary>Builds the toolbar's menus from cached data (no COM on the hot path).</summary>
public static class FolderMenuBuilder
{
    public const int MaxItems = 20;

    public static List<FolderMenuItem> Build(IEnumerable<string?> paths, string? current, string emptyText)
    {
        var list = new List<FolderMenuItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in paths)
        {
            if (string.IsNullOrEmpty(p) || !seen.Add(Core.Util.PathUtil.TrimEndSeparator(p))) continue;
            list.Add(new FolderMenuItem(Core.Util.PathUtil.GetDisplayName(p), p, Core.Util.PathUtil.Equal(p, current)));
            if (list.Count >= MaxItems) break;
        }
        if (list.Count == 0) list.Add(new FolderMenuItem(emptyText, null, false, true));
        return list;
    }
}

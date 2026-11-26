using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using Hopkey.App.Infrastructure;
using Hopkey.Core.Native;
using WinForms = System.Windows.Forms;

namespace Hopkey.App.Tray;

/// <summary>
/// "Arc & Keycap" styling for the WinForms tray menu (docs/UI-REDESIGN.md §9): theme colors, rounded hover
/// highlight with the gradient arc bar, Fluent glyph icons, and DWM rounded corners + brand border.
/// </summary>
internal sealed class TrayMenuRenderer : WinForms.ToolStripRenderer
{
    private sealed record Palette(Color Card, Color Hover, Color Text, Color SubText, Color Accent, Color Divider,
        Color HopTop, Color HopBottom);

    private static Palette Current => ThemeManager.IsDark
        ? new(Hex("#17162C"), Hex("#2A2766"), Hex("#ECEBFF"), Hex("#9D9BC7"), Hex("#A5B4FC"), Hex("#232142"), Hex("#22D3EE"), Hex("#6366F1"))
        : new(Hex("#FFFFFF"), Hex("#E6E4FF"), Hex("#17162B"), Hex("#5E5B7E"), Hex("#4F46E5"), Hex("#E9E7F5"), Hex("#06B6D4"), Hex("#4F46E5"));

    private static Color Hex(string h) => ColorTranslator.FromHtml(h);

    /// <summary>Applies renderer, font, spacing and DWM chrome to the menu; call again after a theme change.</summary>
    public static void Apply(WinForms.ContextMenuStrip menu)
    {
        menu.Renderer = new TrayMenuRenderer();
        menu.ShowImageMargin = true;
        menu.DropShadowEnabled = false; // DWM draws the (rounded) shadow; the WinForms one is square
        menu.Padding = new WinForms.Padding(4, 6, 4, 6);
        // Plain Segoe UI: GDI (which ToolStrip text uses) gets the variable "Segoe UI Variable" line metrics wrong
        // and draws the text too high. CJK falls back to Microsoft YaHei UI either way.
        try { menu.Font = new Font("Segoe UI", 9.75f); }
        catch { menu.Font = WinForms.SystemInformation.MenuFont; }
        menu.BackColor = Current.Card;
        foreach (WinForms.ToolStripItem item in menu.Items)
        {
            if (item is WinForms.ToolStripMenuItem mi)
            {
                mi.Padding = new WinForms.Padding(6, 7, 14, 7);
                mi.ForeColor = Current.Text;
                if (mi.Tag is string glyph)
                {
                    var old = mi.Image;
                    mi.Image = RenderGlyph(glyph, menu.DeviceDpi);
                    old?.Dispose();
                }
            }
        }
    }

    /// <summary>Round corners + brand border on the drop-down HWND (Win11; silently ignored on Win10).</summary>
    public static void ApplyChrome(WinForms.ContextMenuStrip menu)
    {
        var h = menu.Handle;
        Dwmapi.SetRoundCorners(h);
        ThemeManager.ApplyChrome(h, borderless: true);
    }

    private static Bitmap RenderGlyph(string glyph, int dpi)
    {
        int size = (int)Math.Round(16 * dpi / 96.0);
        var bmp = new Bitmap(size, size);
        using var g = Graphics.FromImage(bmp);
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        using var font = new Font("Segoe Fluent Icons", size * 0.75f, GraphicsUnit.Pixel);
        using var brush = new SolidBrush(Current.Accent);
        using var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString(glyph, font, brush, new RectangleF(0, 0, size, size), fmt);
        return bmp;
    }

    protected override void OnRenderToolStripBackground(WinForms.ToolStripRenderEventArgs e)
    {
        using var b = new SolidBrush(Current.Card);
        e.Graphics.FillRectangle(b, e.AffectedBounds);
    }

    // Border is drawn by DWM (rounded, brand color).
    protected override void OnRenderToolStripBorder(WinForms.ToolStripRenderEventArgs e) { }

    protected override void OnRenderImageMargin(WinForms.ToolStripRenderEventArgs e) { }

    protected override void OnRenderMenuItemBackground(WinForms.ToolStripItemRenderEventArgs e)
    {
        if (!e.Item.Selected || !e.Item.Enabled) return;
        var p = Current;
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new Rectangle(2, 1, e.Item.Width - 4, e.Item.Height - 2);
        using (var path = Rounded(r, 6))
        using (var b = new SolidBrush(p.Hover))
            g.FillPath(b, path);
        // The gradient "arc bar" on the left edge, as in the launcher's selected row.
        int barH = Math.Max(10, r.Height / 2);
        var bar = new Rectangle(r.X + 1, r.Y + (r.Height - barH) / 2, 3, barH);
        using (var path = Rounded(bar, 1))
        using (var b = new LinearGradientBrush(bar, p.HopTop, p.HopBottom, LinearGradientMode.Vertical))
            g.FillPath(b, path);
    }

    /// <summary>Icons sit a little right of WinForms' default spot, clear of the arc bar on the left edge.</summary>
    protected override void OnRenderItemImage(WinForms.ToolStripItemImageRenderEventArgs e)
    {
        if (e.Image == null) return;
        int shift = (int)Math.Round(5 * e.Item.Owner!.DeviceDpi / 96.0);
        var r = e.ImageRectangle;
        r.Offset(shift, 0);
        r.Y = (e.Item.Height - r.Height) / 2; // same vertical center as the text
        e.Graphics.DrawImage(e.Image, r);
    }

    protected override void OnRenderItemText(WinForms.ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled ? Current.Text : Current.SubText;
        // Center on the item's full height so the text lines up with the icon, whatever the font's metrics.
        var r = e.TextRectangle;
        e.TextRectangle = new Rectangle(r.X, 0, r.Width, e.Item.Height);
        e.TextFormat = (e.TextFormat & ~(WinForms.TextFormatFlags.Top | WinForms.TextFormatFlags.Bottom))
                       | WinForms.TextFormatFlags.VerticalCenter | WinForms.TextFormatFlags.SingleLine;
        base.OnRenderItemText(e);
    }

    protected override void OnRenderSeparator(WinForms.ToolStripSeparatorRenderEventArgs e)
    {
        int y = e.Item.Height / 2;
        using var pen = new Pen(Current.Divider);
        e.Graphics.DrawLine(pen, 12, y, e.Item.Width - 12, y);
    }

    private static GraphicsPath Rounded(Rectangle r, int radius)
    {
        int d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}

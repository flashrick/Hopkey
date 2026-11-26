using System.Drawing;
using System.Drawing.Drawing2D;
using Hopkey.App.Infrastructure;
using WinForms = System.Windows.Forms;

namespace Hopkey.App.Tray;

/// <summary>Tray icon + menu. Icons are drawn at runtime (no binary assets needed).</summary>
public sealed class TrayIconService : IDisposable
{
    private readonly WinForms.NotifyIcon _icon;
    private readonly Icon _normal;
    private readonly Icon _paused;
    private readonly WinForms.ToolStripMenuItem _pauseItem;
    private readonly WinForms.ToolStripMenuItem _searchItem;
    private readonly WinForms.ToolStripMenuItem _settingsItem;
    private readonly WinForms.ToolStripMenuItem _rebuildItem;
    private readonly WinForms.ToolStripMenuItem _adminItem;
    private readonly WinForms.ToolStripMenuItem _exitItem;

    public event Action? SearchRequested;
    public event Action? SettingsRequested;
    public event Action? RebuildRequested;
    public event Action? PauseToggled;
    public event Action? ElevateRequested;
    public event Action? ExitRequested;

    public TrayIconService(bool elevated)
    {
        _normal = CreateIcon(Color.FromArgb(0, 103, 192));
        _paused = CreateIcon(Color.FromArgb(128, 128, 128));
        var menu = new WinForms.ContextMenuStrip();
        _searchItem = new WinForms.ToolStripMenuItem("", null, (_, _) => SearchRequested?.Invoke());
        _settingsItem = new WinForms.ToolStripMenuItem("", null, (_, _) => SettingsRequested?.Invoke());
        _rebuildItem = new WinForms.ToolStripMenuItem("", null, (_, _) => RebuildRequested?.Invoke());
        _pauseItem = new WinForms.ToolStripMenuItem("", null, (_, _) => PauseToggled?.Invoke());
        _adminItem = new WinForms.ToolStripMenuItem("", null, (_, _) => ElevateRequested?.Invoke()) { Visible = !elevated };
        _exitItem = new WinForms.ToolStripMenuItem("", null, (_, _) => ExitRequested?.Invoke());
        menu.Items.AddRange(new WinForms.ToolStripItem[]
        {
            _searchItem, _settingsItem, new WinForms.ToolStripSeparator(), _rebuildItem, _pauseItem, _adminItem,
            new WinForms.ToolStripSeparator(), _exitItem,
        });
        _icon = new WinForms.NotifyIcon { Icon = _normal, ContextMenuStrip = menu, Visible = true };
        _icon.MouseClick += (_, e) => { if (e.Button == WinForms.MouseButtons.Left) SearchRequested?.Invoke(); };
        _icon.DoubleClick += (_, _) => SettingsRequested?.Invoke();
        UpdateTexts(false);
    }

    public void UpdateTexts(bool paused)
    {
        _searchItem.Text = LocalizationManager.T("Search");
        _settingsItem.Text = LocalizationManager.T("Settings");
        _rebuildItem.Text = LocalizationManager.T("RebuildIndex");
        _pauseItem.Text = LocalizationManager.T(paused ? "Resume" : "Pause");
        _adminItem.Text = LocalizationManager.T("Tray.RunAsAdmin");
        _exitItem.Text = LocalizationManager.T("Exit");
        var tip = LocalizationManager.T("Tray.Tooltip") + (paused ? " (" + LocalizationManager.T("Paused") + ")" : "");
        _icon.Text = tip.Length > 63 ? tip[..63] : tip;
        _icon.Icon = paused ? _paused : _normal;
    }

    public void ShowBalloon(string text, bool warning = false)
    {
        _icon.BalloonTipTitle = "Hopkey";
        _icon.BalloonTipText = text;
        _icon.BalloonTipIcon = warning ? WinForms.ToolTipIcon.Warning : WinForms.ToolTipIcon.Info;
        _icon.ShowBalloonTip(4000);
    }

    /// <summary>Draws a rounded-square magnifier icon.</summary>
    public static Icon CreateIcon(Color bg, int size = 32)
    {
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var path = new GraphicsPath();
            float r = size * 0.22f;
            var rect = new RectangleF(1, 1, size - 2, size - 2);
            path.AddArc(rect.X, rect.Y, r * 2, r * 2, 180, 90);
            path.AddArc(rect.Right - r * 2, rect.Y, r * 2, r * 2, 270, 90);
            path.AddArc(rect.Right - r * 2, rect.Bottom - r * 2, r * 2, r * 2, 0, 90);
            path.AddArc(rect.X, rect.Bottom - r * 2, r * 2, r * 2, 90, 90);
            path.CloseFigure();
            using (var b = new SolidBrush(bg)) g.FillPath(b, path);
            using var pen = new Pen(Color.White, size * 0.11f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            float c = size * 0.42f, rad = size * 0.2f;
            g.DrawEllipse(pen, c - rad, c - rad, rad * 2, rad * 2);
            g.DrawLine(pen, c + rad * 0.72f, c + rad * 0.72f, size * 0.78f, size * 0.78f);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}

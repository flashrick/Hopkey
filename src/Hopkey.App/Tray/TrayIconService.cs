using System.Drawing;
using System.Drawing.Drawing2D;
using Hopkey.App.Infrastructure;
using WinForms = System.Windows.Forms;

namespace Hopkey.App.Tray;

/// <summary>Tray icon + menu. Uses the embedded app icon; the paused state is a grayscale copy.</summary>
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
        _normal = LoadAppIcon(WinForms.SystemInformation.SmallIconSize);
        _paused = CreateGrayscale(_normal);
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

    /// <summary>Loads the embedded app icon (Assets/hopkey.ico) at the closest available size.</summary>
    public static Icon LoadAppIcon(Size size)
    {
        using var stream = typeof(TrayIconService).Assembly.GetManifestResourceStream("Hopkey.App.Assets.hopkey.ico")
            ?? throw new InvalidOperationException("Embedded resource Assets/hopkey.ico is missing.");
        return new Icon(stream, size);
    }

    /// <summary>Desaturated, slightly faded copy of an icon (used for the paused state).</summary>
    private static Icon CreateGrayscale(Icon source)
    {
        using var src = source.ToBitmap();
        using var bmp = new Bitmap(src.Width, src.Height);
        using (var g = Graphics.FromImage(bmp))
        {
            var m = new System.Drawing.Imaging.ColorMatrix(new[]
            {
                new[] { 0.30f, 0.30f, 0.30f, 0, 0 },
                new[] { 0.59f, 0.59f, 0.59f, 0, 0 },
                new[] { 0.11f, 0.11f, 0.11f, 0, 0 },
                new[] { 0f, 0, 0, 0.85f, 0 },
                new[] { 0f, 0, 0, 0, 1 },
            });
            using var attrs = new System.Drawing.Imaging.ImageAttributes();
            attrs.SetColorMatrix(m);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(src, new Rectangle(0, 0, src.Width, src.Height), 0, 0, src.Width, src.Height, GraphicsUnit.Pixel, attrs);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}

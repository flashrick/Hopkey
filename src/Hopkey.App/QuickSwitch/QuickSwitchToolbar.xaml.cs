using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Hopkey.App.Infrastructure;
using Hopkey.Core.Native;

namespace Hopkey.App.QuickSwitch;

/// <summary>Data/actions the toolbar needs. Getters must be thread-safe; actions may be invoked from the toolbar thread.</summary>
public interface IQuickSwitchToolbarActions
{
    IReadOnlyList<FolderMenuItem> GetExplorerFolders();
    IReadOnlyList<FolderMenuItem> GetFavorites();
    IReadOnlyList<FolderMenuItem> GetRecent();
    string? GetRecommended();
    void NavigateTo(string path);
    void OpenSearch();
    void OpenSettings();
    void DisableAutoForCurrentApp();
    void DisableForCurrentApp();
    void AddCurrentToFavorites();
    void RestartElevated();
}

public partial class QuickSwitchToolbar : Window
{
    private readonly IQuickSwitchToolbarActions _actions;
    public IntPtr Handle { get; private set; }
    public ToolbarSide Side { get; set; } = ToolbarSide.Below;
    private string? _recommended;

    public QuickSwitchToolbar(IQuickSwitchToolbarActions actions)
    {
        _actions = actions;
        InitializeComponent();
        ApplyResources();
        SourceInitialized += (_, _) =>
        {
            Handle = new WindowInteropHelper(this).Handle;
            long ex = User32.GetWindowLongPtr(Handle, User32.GWL_EXSTYLE).ToInt64();
            ex |= User32.WS_EX_TOOLWINDOW | User32.WS_EX_NOACTIVATE;
            ex &= ~User32.WS_EX_APPWINDOW;
            User32.SetWindowLongPtr(Handle, User32.GWL_EXSTYLE, new IntPtr(ex));
            Dwmapi.SetRoundCorners(Handle, small: true);
            HwndSource.FromHwnd(Handle)?.AddHook(WndProc);
        };
    }

    public void ApplyResources()
    {
        ThemeManager.ReplaceDictionary(Resources, ThemeManager.CreateDictionary(ThemeManager.IsDark));
        LocalizationManager.ApplyTo(Resources);
    }

    /// <summary>Physical-pixel position the host wants; enforced against anything else moving the window (WPF layout, re-shows).</summary>
    public (int X, int Y)? DesiredPosition { get; set; }

    /// <summary>The dialog that owns us while attached.</summary>
    public IntPtr OwnerDialog { get; set; }

    private int _menusOpen;
    public int MenusOpen => _menusOpen;

    private const int WM_WINDOWPOSCHANGING = 0x0046;
    private const int WM_ACTIVATE = 0x0006;

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct WINDOWPOS
    {
        public IntPtr hwnd, hwndInsertAfter;
        public int x, y, cx, cy;
        public uint flags;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case User32.WM_MOUSEACTIVATE:
                handled = true;
                return new IntPtr(User32.MA_NOACTIVATE);
            case WM_WINDOWPOSCHANGING when DesiredPosition is { } pos:
                unsafe
                {
                    var wp = (WINDOWPOS*)lParam;
                    if ((wp->flags & User32.SWP_NOMOVE) == 0) { wp->x = pos.X; wp->y = pos.Y; }
                }
                break;
            case WM_ACTIVATE when (wParam.ToInt64() & 0xFFFF) != 0 && _menusOpen == 0 && OwnerDialog != IntPtr.Zero:
                // Something (an external ShowWindow, a click) activated us: give the foreground back to the dialog.
                var owner = OwnerDialog;
                Dispatcher.BeginInvoke(() => User32.SetForegroundWindow(owner));
                break;
        }
        return IntPtr.Zero;
    }

    public void SetRecommended(string? path)
    {
        _recommended = path;
        if (string.IsNullOrEmpty(path))
        {
            RecommendButton.Visibility = Visibility.Collapsed;
            return;
        }
        RecommendText.Text = Core.Util.PathUtil.GetDisplayName(path);
        RecommendButton.ToolTip = LocalizationManager.T("QS.Recommended") + "\n" + path;
        RecommendButton.Visibility = Visibility.Visible;
    }

    public void SetElevatedWarning(bool show) => WarningButton.Visibility = show ? Visibility.Visible : Visibility.Collapsed;

    public void SetBusy(bool busy)
    {
        BusyIndicator.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        if (busy)
            SpinnerRotate.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(800)) { RepeatBehavior = RepeatBehavior.Forever });
        else
            SpinnerRotate.BeginAnimation(RotateTransform.AngleProperty, null);
    }

    public async void FlashError(string message)
    {
        SetBusy(false);
        var tip = new ToolTip { Content = message, PlacementTarget = SearchButton, Placement = PlacementMode.Bottom, IsOpen = true };
        var old = Background;
        Background = (Brush)Resources["Brush.Error"];
        await Task.Delay(250);
        Background = old;
        await Task.Delay(1750);
        tip.IsOpen = false;
    }

    private void OnSearch(object sender, RoutedEventArgs e)
    {
        Core.Util.Logger.Debug("Toolbar search button clicked");
        _actions.OpenSearch();
    }

    private void OnRecommend(object sender, RoutedEventArgs e)
    {
        if (_recommended != null) _actions.NavigateTo(_recommended);
    }

    private void OnWarning(object sender, RoutedEventArgs e) => _actions.RestartElevated();

    private void OnExplorerMenu(object sender, RoutedEventArgs e) =>
        ShowFolderMenu((Button)sender, _actions.GetExplorerFolders());

    private void OnFavoritesMenu(object sender, RoutedEventArgs e) =>
        ShowFolderMenu((Button)sender, _actions.GetFavorites());

    private void OnRecentMenu(object sender, RoutedEventArgs e) =>
        ShowFolderMenu((Button)sender, _actions.GetRecent());

    private void OnMoreMenu(object sender, RoutedEventArgs e)
    {
        var menu = CreateMenu((Button)sender);
        menu.Items.Add(MakeItem(LocalizationManager.T("QS.AddCurrentToFavorites"), null, false, false, _actions.AddCurrentToFavorites));
        menu.Items.Add(new Separator());
        menu.Items.Add(MakeItem(LocalizationManager.T("QS.DisableAutoForApp"), null, false, false, _actions.DisableAutoForCurrentApp));
        menu.Items.Add(MakeItem(LocalizationManager.T("QS.DisableForApp"), null, false, false, _actions.DisableForCurrentApp));
        menu.Items.Add(new Separator());
        menu.Items.Add(MakeItem(LocalizationManager.T("QS.Settings"), null, false, false, _actions.OpenSettings));
        menu.IsOpen = true;
    }

    private ContextMenu CreateMenu(Button target)
    {
        var menu = new ContextMenu
        {
            PlacementTarget = target,
            Placement = Side == ToolbarSide.Above ? PlacementMode.Top : PlacementMode.Bottom,
            Background = (Brush)Resources["Brush.Card"],
            Foreground = (Brush)Resources["Brush.Text"],
            BorderBrush = (Brush)Resources["Brush.Border"],
            FontFamily = (FontFamily)Resources["Font.Family"],
        };
        menu.Opened += (_, _) => _menusOpen++;
        menu.Closed += (_, _) => _menusOpen = Math.Max(0, _menusOpen - 1);
        return menu;
    }

    private void ShowFolderMenu(Button target, IReadOnlyList<FolderMenuItem> items)
    {
        var menu = CreateMenu(target);
        foreach (var it in items)
        {
            var path = it.Path;
            menu.Items.Add(MakeItem(it.Title, it.Path, it.IsCurrent, it.IsDisabled, path == null ? null : () => _actions.NavigateTo(path)));
        }
        menu.IsOpen = true;
    }

    private MenuItem MakeItem(string title, string? subtitle, bool isCurrent, bool disabled, Action? onClick)
    {
        var panel = new StackPanel { Orientation = Orientation.Vertical, MaxWidth = 420 };
        panel.Children.Add(new TextBlock { Text = title, FontWeight = subtitle != null ? FontWeights.SemiBold : FontWeights.Normal, TextTrimming = TextTrimming.CharacterEllipsis });
        if (subtitle != null)
            panel.Children.Add(new TextBlock
            {
                Text = Core.Util.PathUtil.Ellipsize(subtitle, 70),
                FontSize = 11,
                Foreground = (Brush)Resources["Brush.SubText"],
            });
        var item = new MenuItem
        {
            Header = panel,
            IsEnabled = !disabled,
            IsCheckable = false,
            IsChecked = isCurrent,
            ToolTip = subtitle,
            Icon = subtitle != null ? new TextBlock
            {
                Text = "",
                FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                Foreground = (Brush)Resources["Brush.Accent"],
                VerticalAlignment = VerticalAlignment.Center,
            } : null,
        };
        if (onClick != null) item.Click += (_, _) => onClick();
        return item;
    }
}

using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Hopkey.App.Infrastructure;
using Hopkey.Core.Native;
using Hopkey.Core.Search;
using Hopkey.Core.Util;

namespace Hopkey.App.Launcher;

public partial class LauncherWindow : Window
{
    private readonly LauncherViewModel _vm;
    private readonly ActionExecutor _executor;
    private readonly Func<LauncherContext, SearchResult, bool, Task<bool>> _contextNavigate;
    private readonly Action _openLog;
    private IntPtr _hwnd;
    private bool _suppressHide;
    private bool _visible;
    private readonly DispatcherTimer _trimTimer;
    private long _showStart;
    private int _reactivations;
    private IconCache? _icons;

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    public bool IsShown => _visible;

    public LauncherWindow(LauncherViewModel vm, ActionExecutor executor, IconCache icons,
        Func<LauncherContext, SearchResult, bool, Task<bool>> contextNavigate, Action openLog)
    {
        _vm = vm;
        _executor = executor;
        _contextNavigate = contextNavigate;
        _openLog = openLog;
        _icons = icons;
        DataContext = vm;
        InitializeComponent();
        _trimTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _trimTimer.Tick += (_, _) =>
        {
            _trimTimer.Stop();
            if (!_visible) { _vm.ReleaseResults(); _icons?.TrimPathCache(); }
        };
        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            long ex = User32.GetWindowLongPtr(_hwnd, User32.GWL_EXSTYLE).ToInt64();
            ex |= User32.WS_EX_TOOLWINDOW;
            User32.SetWindowLongPtr(_hwnd, User32.GWL_EXSTYLE, new IntPtr(ex));
            Dwmapi.SetRoundCorners(_hwnd);
            Dwmapi.EnableShadow(_hwnd);
            // Win11 draws a rounded brand border via DWM (ApplyWindowChrome); Win10 needs WPF's square one.
            if (!Dwmapi.IsWin11) BorderThickness = new Thickness(1);
            ThemeManager.ApplyWindowChrome(this);
        };
        Activated += (_, _) => Logger.Debug("Launcher activated");
        Deactivated += (_, _) =>
        {
            var fg = User32.GetForegroundWindow();
            Logger.Debug($"Launcher deactivated → foreground 0x{fg:X} {User32.GetClassName(fg)} (pid {User32.GetProcessId(fg)})");
            if (_suppressHide || !_visible) return;
            // Losing activation within a few hundred ms of showing is never the user's doing (Windows can hand the
            // foreground back to the previous window after a hook-triggered activation): take it back instead of hiding.
            if (System.Diagnostics.Stopwatch.GetElapsedTime(_showStart).TotalMilliseconds < 300 && _reactivations++ < 2)
            {
                Logger.Debug("Launcher lost activation right after show; re-activating");
                Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
                {
                    if (!_visible) return;
                    ForegroundHelper.Activate(_hwnd);
                    Activate();
                    QueryBox.Focus();
                });
                return;
            }
            HideLauncher();
        };
        PreviewKeyDown += OnPreviewKeyDown;
        vm.PropertyChanged += OnVmPropertyChanged;
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(LauncherViewModel.SelectedIndex):
                if (_vm.SelectedIndex >= 0 && _vm.SelectedIndex < _vm.Results.Count)
                    ResultList.ScrollIntoView(_vm.Results[_vm.SelectedIndex]);
                break;
            case nameof(LauncherViewModel.IndexError):
                StatusText.SetResourceReference(ForegroundProperty, _vm.IndexError ? "Brush.Error" : "Brush.SubText");
                StatusText.Cursor = _vm.IndexError ? Cursors.Hand : null;
                break;
            case nameof(LauncherViewModel.ActionMode):
                ResultList.Visibility = _vm.ActionMode ? Visibility.Hidden : Visibility.Visible;
                if (_vm.ActionMode && _visible && SystemParameters.ClientAreaAnimation)
                {
                    ActionPanel.BeginAnimation(OpacityProperty, s_actionFade);
                    ActionShift.BeginAnimation(TranslateTransform.XProperty, s_actionSlide);
                }
                break;
        }
    }

    // Open / action-mode motion (docs/UI-REDESIGN.md §10). Frozen and shared: no per-show allocation.
    private static readonly IEasingFunction s_out = Frozen(new CubicEase { EasingMode = EasingMode.EaseOut });
    private static readonly DoubleAnimation s_fade = Frozen(new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(110)) { EasingFunction = s_out });
    private static readonly DoubleAnimation s_sweep = Frozen(new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150)) { EasingFunction = s_out });
    private static readonly DoubleAnimation s_hop = Frozen(new DoubleAnimation(-3, 0, TimeSpan.FromMilliseconds(150))
        { EasingFunction = Frozen(new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.6 }) });
    private static readonly DoubleAnimation s_actionFade = Frozen(new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(100)) { EasingFunction = s_out });
    private static readonly DoubleAnimation s_actionSlide = Frozen(new DoubleAnimation(10, 0, TimeSpan.FromMilliseconds(120)) { EasingFunction = s_out });

    private static T Frozen<T>(T f) where T : Freezable { f.Freeze(); return f; }

    private void PlayOpenAnimation()
    {
        if (!SystemParameters.ClientAreaAnimation) return;
        Root.BeginAnimation(OpacityProperty, s_fade);
        HopLineScale.BeginAnimation(ScaleTransform.ScaleXProperty, s_sweep);
        HopGlyphShift.BeginAnimation(TranslateTransform.YProperty, s_hop);
    }

    /// <summary>Create the HWND and run first layout/template instantiation off-screen so the first show is instant.</summary>
    public void Prewarm()
    {
        // Create the HWND and build/measure the visual tree without ever showing the window: an invisible
        // Show/Hide cycle here made the first real Show lose activation right after gaining it.
        new WindowInteropHelper(this).EnsureHandle();
        _vm.SearchNow();
        var content = (System.Windows.UIElement)Content;
        content.Measure(new Size(Width, double.PositiveInfinity));
        content.Arrange(new Rect(content.DesiredSize));
        content.UpdateLayout();
        _visible = false;
    }

    public void Toggle(LauncherContext ctx)
    {
        if (_visible && !ctx.IsContext) HideLauncher();
        else ShowLauncher(ctx, null);
    }

    public void ShowLauncher(LauncherContext ctx, string? initialText)
    {
        _showStart = PerfLog.Start();
        _reactivations = 0;
        _trimTimer.Stop();
        _vm.Context = ctx;
        // Every show starts fresh: empty query and the default (empty-query) result list.
        _vm.Reset(clearQuery: true);
        if (initialText != null) _vm.Query = initialText;
        else _vm.SearchNow();

        PositionWindow(ctx);
        if (!IsVisible) Show();
        _visible = true;
        PlayOpenAnimation();
        bool activated = ForegroundHelper.Activate(_hwnd);
        Logger.Debug($"Launcher Activate() → {activated}, foreground now 0x{User32.GetForegroundWindow():X}");
        Activate();
        QueryBox.Focus();
        Keyboard.Focus(QueryBox);
        if (initialText != null) QueryBox.CaretIndex = QueryBox.Text.Length;
        else QueryBox.SelectAll();
        Dispatcher.BeginInvoke(DispatcherPriority.Render, () => PerfLog.Stop("launcher.show", _showStart));
    }

    public void HideLauncher()
    {
        if (!_visible) return;
        _visible = false;
        _vm.ActionMode = false;
        Hide();
        _trimTimer.Start();
    }

    /// <summary>Another process's window became the foreground window (WinEvent). Hide like a click-away.</summary>
    public void OnExternalForeground(IntPtr hwnd)
    {
        if (!_visible || _suppressHide || hwnd == _hwnd) return;
        // Right after showing, Windows may briefly hand the foreground back to the previous window; the Deactivated
        // handler re-activates in that window, so don't hide here.
        if (System.Diagnostics.Stopwatch.GetElapsedTime(_showStart).TotalMilliseconds < 300) return;
        Logger.Debug($"Foreground moved to 0x{hwnd:X} {User32.GetClassName(hwnd)}; hiding launcher");
        HideLauncher();
    }

    private void PositionWindow(LauncherContext ctx)
    {
        IntPtr monitor;
        RECT anchor = default;
        bool anchored = ctx.IsContext && ctx.Hwnd != IntPtr.Zero && User32.IsWindow(ctx.Hwnd);
        if (anchored)
        {
            anchor = Dwmapi.GetExtendedFrameBounds(ctx.Hwnd);
            monitor = User32.MonitorFromWindow(ctx.Hwnd, User32.MONITOR_DEFAULTTONEAREST);
        }
        else
        {
            User32.GetCursorPos(out var p);
            monitor = User32.MonitorFromPoint(p, User32.MONITOR_DEFAULTTONEAREST);
        }
        var work = User32.GetMonitorWorkArea(monitor);
        double scale = GetDpiForMonitor(monitor, 0, out uint dpi, out _) == 0 ? dpi / 96.0 : 1.0;
        int width = (int)Math.Round(Width * scale);
        int x, y;
        if (anchored)
        {
            x = anchor.Left + (anchor.Width - width) / 2;
            y = anchor.Top + (int)(80 * scale);
        }
        else
        {
            x = work.Left + (work.Width - width) / 2;
            y = work.Top + (int)(work.Height * 0.22);
        }
        x = Math.Clamp(x, work.Left, Math.Max(work.Left, work.Right - width));
        y = Math.Clamp(y, work.Top, Math.Max(work.Top, work.Bottom - (int)(300 * scale)));
        User32.SetWindowPos(_hwnd, User32.HWND_TOPMOST, x, y, 0, 0, User32.SWP_NOSIZE | User32.SWP_NOACTIVATE);
    }

    private void OnQueryTextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) =>
        Placeholder.Visibility = string.IsNullOrEmpty(QueryBox.Text) ? Visibility.Visible : Visibility.Collapsed;

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var mods = Keyboard.Modifiers;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        switch (key)
        {
            case Key.Down: _vm.MoveSelection(1); e.Handled = true; break;
            case Key.Up: _vm.MoveSelection(-1); e.Handled = true; break;
            case Key.PageDown: _vm.MoveSelection(8); e.Handled = true; break;
            case Key.PageUp: _vm.MoveSelection(-8); e.Handled = true; break;
            case Key.Home when mods.HasFlag(ModifierKeys.Control): _vm.SelectEdge(true); e.Handled = true; break;
            case Key.End when mods.HasFlag(ModifierKeys.Control): _vm.SelectEdge(false); e.Handled = true; break;
            case Key.Escape:
                if (_vm.ActionMode) _vm.ActionMode = false;
                else if (!string.IsNullOrEmpty(_vm.Query)) _vm.Query = "";
                else HideLauncher();
                e.Handled = true;
                break;
            case Key.Tab when mods.HasFlag(ModifierKeys.Control):
                if (!_vm.ActionMode && _vm.CycleFacet(mods.HasFlag(ModifierKeys.Shift) ? -1 : 1))
                    QueryBox.CaretIndex = QueryBox.Text.Length;
                e.Handled = true;
                break;
            case Key.Tab:
                if (_vm.ActionMode) _vm.ActionMode = false; else _vm.EnterActionMode();
                e.Handled = true;
                break;
            case Key.Right when QueryBox.CaretIndex == QueryBox.Text.Length && QueryBox.SelectionLength == 0 && !_vm.ActionMode:
                if (_vm.EnterActionMode()) e.Handled = true;
                break;
            case Key.Left when _vm.ActionMode:
                _vm.ActionMode = false;
                e.Handled = true;
                break;
            case Key.Enter:
                e.Handled = true;
                if (_vm.ActionMode)
                {
                    if (_vm.SelectedAction is { } a) _ = RunAction(a.Kind);
                }
                else if (mods.HasFlag(ModifierKeys.Control)) _ = RunAction(ResultActionKind.OpenFolder);
                else if (mods.HasFlag(ModifierKeys.Shift)) _ = RunAction(ResultActionKind.RunAsAdmin);
                else _ = RunAction(ResultActionKind.Open);
                break;
            case Key.Delete when mods == ModifierKeys.Shift && QueryBox.SelectionLength == 0 && _vm.Selected != null:
                _ = RunAction(ResultActionKind.Delete);
                e.Handled = true;
                break;
            case Key.C when mods == ModifierKeys.Control && QueryBox.SelectionLength == 0 && _vm.Selected != null:
                _ = RunAction(ResultActionKind.CopyPath);
                e.Handled = true;
                break;
            case >= Key.D1 and <= Key.D9 when mods == ModifierKeys.Alt:
            {
                int idx = key - Key.D1;
                if (idx < _vm.Results.Count)
                {
                    _vm.SelectedIndex = idx;
                    _ = RunAction(ResultActionKind.Open);
                }
                e.Handled = true;
                break;
            }
        }
    }

    // List items are Focusable=False (focus stays in the query box), so a click does NOT move the ListBox selection.
    // Resolve the item under the mouse ourselves, otherwise the click would act on whatever was selected before.
    private static int ClickedIndex(System.Windows.Controls.ListBox list, MouseButtonEventArgs e) =>
        e.OriginalSource is DependencyObject d && System.Windows.Controls.ItemsControl.ContainerFromElement(list, d) is System.Windows.Controls.ListBoxItem item
            ? list.ItemContainerGenerator.IndexFromContainer(item) : -1;

    private void OnResultDoubleClick(object sender, MouseButtonEventArgs e)
    {
        int idx = ClickedIndex(ResultList, e);
        if (idx < 0 || idx >= _vm.Results.Count) return;
        _vm.SelectedIndex = idx;
        _ = RunAction(ResultActionKind.Open);
    }

    private void OnActionClick(object sender, MouseButtonEventArgs e)
    {
        int idx = ClickedIndex(ActionList, e);
        if (idx < 0 || idx >= _vm.Actions.Count) return;
        _vm.SelectedActionIndex = idx;
        e.Handled = true;
        if (_vm.SelectedAction is { } a) _ = RunAction(a.Kind);
    }

    /// <summary>Right-click a result: select it and open the same action list as Tab.</summary>
    private void OnResultRightClick(object sender, MouseButtonEventArgs e)
    {
        var item = System.Windows.Controls.ItemsControl.ContainerFromElement(ResultList, (DependencyObject)e.OriginalSource)
            as System.Windows.Controls.ListBoxItem;
        if (item?.DataContext is not ResultItemViewModel vm) return;
        int idx = -1;
        for (int i = 0; i < _vm.Results.Count; i++) if (ReferenceEquals(_vm.Results[i], vm)) { idx = i; break; }
        if (idx < 0) return;
        _vm.SelectedIndex = idx;
        _vm.EnterActionMode();
        QueryBox.Focus();
        e.Handled = true;
    }

    private void OnFacetClick(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not FacetViewModel f) return;
        _vm.ApplyFacet(f, recordUsage: true);
        QueryBox.Focus();
        QueryBox.CaretIndex = QueryBox.Text.Length;
        e.Handled = true;
    }

    private void OnBackClick(object sender, MouseButtonEventArgs e)
    {
        _vm.ActionMode = false;
        QueryBox.Focus();
        e.Handled = true;
    }

    private void OnStatusClick(object sender, MouseButtonEventArgs e)
    {
        if (_vm.IndexError) _openLog();
    }

    private async Task RunAction(ResultActionKind action)
    {
        var sel = _vm.Selected;
        if (sel == null) return;
        var r = sel.Result;
        var query = _vm.Query;
        var ctx = _vm.Context;
        try
        {
            // Context mode: folders/files navigate the target Explorer window / dialog instead of opening new windows.
            if (ctx.IsContext && action == ResultActionKind.Open && r.Kind is ResultKind.Folder or ResultKind.File)
            {
                HideLauncher();
                _executor.RecordLaunch(r, query);
                bool ok = await _contextNavigate(ctx, r, r.Kind == ResultKind.File);
                if (ok) return;
                // fall back to a normal open
            }
            _suppressHide = true;
            bool hide = _executor.Execute(r, action, query, this);
            _suppressHide = false;
            if (hide) HideLauncher();
            else
            {
                if (action is ResultActionKind.ExcludeFolder or ResultActionKind.ToggleFavorite or ResultActionKind.RemoveHistory or ResultActionKind.Delete)
                {
                    _vm.ActionMode = false;
                    _vm.SearchNow();
                }
                if (action is ResultActionKind.CopyPath or ResultActionKind.CopyFile or ResultActionKind.CopyResult)
                    _vm.ShowMessage(LocalizationManager.T("Launcher.Copied"));
                if (_visible) { Activate(); QueryBox.Focus(); }
            }
        }
        catch (Exception ex)
        {
            _suppressHide = false;
            Logger.Warn($"Action {action} failed for {r.Path}", ex);
            if (!_visible) ShowLauncher(ctx, null);
            _vm.ShowMessage(LocalizationManager.F("Launcher.OpenFailed", ex.Message), error: true);
        }
    }
}

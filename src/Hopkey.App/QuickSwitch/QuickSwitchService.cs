using System.Windows.Threading;
using Hopkey.App.Explorer;
using Hopkey.App.Hooks;
using Hopkey.App.Infrastructure;
using Hopkey.Core.Config;
using Hopkey.Core.History;
using Hopkey.Core.Native;
using Hopkey.Core.Util;

namespace Hopkey.App.QuickSwitch;

/// <summary>
/// File dialog state machine (UI thread). Features: floating toolbar, auto-switch on open (off by default),
/// follow Explorer when switching back (once per Explorer activation), Ctrl+G jump, per-app opt-outs.
/// </summary>
public sealed class QuickSwitchService : IQuickSwitchToolbarActions, IDisposable
{
    private sealed class DialogState
    {
        public required FileDialogInfo Info;
        public DateTime OpenedAtUtc = DateTime.UtcNow;
        public bool Seen;
        public long LastAppliedSeq = -1;
        public readonly SemaphoreSlim Lock = new(1, 1);
    }

    private readonly Dispatcher _ui;
    private readonly WinEventHub _events;
    private readonly ExplorerService _explorer;
    private readonly FileDialogNavigator _navigator = new();
    private readonly HotkeyService _hotkeys;
    private readonly HistoryStore _history;
    private readonly SettingsStore _settings;
    private readonly ToolbarHost _toolbar;
    private readonly Dictionary<IntPtr, DialogState> _dialogs = new();
    private QuickSwitchSettings _qs;
    private bool _paused;
    private DialogState? _attached;
    private volatile FileDialogInfo? _active;

    /// <summary>Raised when the user clicks the toolbar search button (open launcher in context mode for this dialog).</summary>
    public event Action<FileDialogInfo>? SearchRequested;
    public event Action? SettingsRequested;
    public event Action? ElevationRequested;
    public event Action<FileDialogInfo?>? ActiveDialogChanged;

    public FileDialogInfo? ActiveDialog => _active;

    public QuickSwitchService(Dispatcher ui, WinEventHub events, ExplorerService explorer, HotkeyService hotkeys, HistoryStore history, SettingsStore settings)
    {
        _ui = ui;
        _events = events;
        _explorer = explorer;
        _hotkeys = hotkeys;
        _history = history;
        _settings = settings;
        _qs = settings.Current.QuickSwitch;
        _toolbar = new ToolbarHost(this);

        _events.Foreground += OnForeground;
        _events.Destroyed += OnDestroyed;
        _events.Hidden += OnHiddenOrEnded;
        _events.DialogEnded += OnHiddenOrEnded;
        _events.MinimizeStart += OnHiddenOrEnded;
        _events.MinimizeEnd += OnMinimizeEnd;
        _events.LocationChanged += OnLocationChanged;
        _events.ExplorerNameChanged += h => _explorer.NotifyTitleChanged(h);
        _hotkeys.DialogJumpRequested += OnDialogJump;
    }

    public void Apply(QuickSwitchSettings s, bool paused)
    {
        _qs = s;
        _paused = paused;
        if (paused || !s.Enabled || !s.ShowToolbar) DetachToolbar();
        if (paused || !s.Enabled) SetActive(null);
    }

    public void RefreshResources() => _toolbar.RefreshResources();

    /// <summary>Evaluate the current foreground window (no event fires for a dialog that was already active when we started).</summary>
    public void CheckCurrentForeground()
    {
        var fg = User32.GetForegroundWindow();
        if (fg != IntPtr.Zero && User32.GetProcessId(fg) != (uint)Environment.ProcessId) OnForeground(fg);
    }

    // ------------------------------------------------------------------ events

    private void OnForeground(IntPtr hwnd) => OnForeground(hwnd, 0);

    private void OnForeground(IntPtr hwnd, int retry)
    {
        if (_paused) return;
        Logger.Debug($"Foreground 0x{hwnd:X} class={User32.GetClassName(hwnd)}");
        // The toolbar can't be owned by an elevated dialog (UIPI) and then floats topmost: hide it when another
        // app's window comes to the front. Our own windows (launcher, toolbar menus) don't count.
        if (_attached != null && hwnd != _attached.Info.Hwnd && User32.GetProcessId(hwnd) != (uint)Environment.ProcessId)
            _toolbar.OwnerDeactivated();
        if (WindowInfoCache.IsExplorer(hwnd))
        {
            _events.TrackExplorer(hwnd);
            _explorer.NotifyActivated(hwnd);
            return;
        }
        if (!_qs.Enabled) return;
        if (!FileDialogDetector.IsDialogClass(hwnd))
        {
            // Another window became foreground; the toolbar (owned by the dialog) naturally follows z-order.
            SetActive(null);
            return;
        }
        if (!FileDialogDetector.TryDetect(hwnd, _qs.DisabledProcesses, out var info))
        {
            // A dialog can take the foreground before it is visible / before its child controls exist. Retry briefly.
            if (retry < 6)
            {
                int delay = 15 << retry; // 15, 30, 60, 120, 240, 480 ms
                Logger.Debug($"#32770 0x{hwnd:X} not (yet) a file dialog; retry {retry + 1} in {delay}ms");
                var timer = new DispatcherTimer(DispatcherPriority.Normal, _ui) { Interval = TimeSpan.FromMilliseconds(delay) };
                timer.Tick += (_, _) =>
                {
                    timer.Stop();
                    if (User32.GetForegroundWindow() == hwnd) OnForeground(hwnd, retry + 1);
                };
                timer.Start();
            }
            SetActive(null);
            return;
        }

        if (!_dialogs.TryGetValue(hwnd, out var d))
        {
            d = new DialogState { Info = info };
            _dialogs[hwnd] = d;
            _events.Track(hwnd);
            Logger.Info($"QuickSwitch attached: {info}");
        }
        else d.Info = info;
        SetActive(info);

        if (_qs.ShowToolbar) AttachToolbar(d);

        if (IsAutoExcluded(info.ProcessName)) { d.Seen = true; return; }
        var last = _explorer.LastActive;
        if (!d.Seen)
        {
            d.Seen = true;
            if (_qs.AutoSwitchOnOpen && last != null && DateTime.UtcNow - last.AtUtc < TimeSpan.FromMinutes(Math.Max(1, _qs.AutoSwitchMaxAgeMinutes)))
            {
                d.LastAppliedSeq = last.Seq;
                _ = NavigateFromExplorerAsync(d, last.Hwnd);
            }
        }
        else if (_qs.FollowExplorer && last != null && last.Seq != d.LastAppliedSeq && last.AtUtc > d.OpenedAtUtc)
        {
            d.LastAppliedSeq = last.Seq; // once per Explorer activation
            _ = NavigateFromExplorerAsync(d, last.Hwnd);
        }
    }

    private void SetActive(FileDialogInfo? info)
    {
        if (ReferenceEquals(_active, info)) return;
        _active = info;
        _hotkeys.ActiveDialog = info?.Hwnd ?? IntPtr.Zero;
        ActiveDialogChanged?.Invoke(info);
    }

    private bool IsAutoExcluded(string process) =>
        _qs.AutoSwitchExcludedProcesses.Any(p => string.Equals(p, process, StringComparison.OrdinalIgnoreCase));

    private void AttachToolbar(DialogState d)
    {
        if (_attached != null && _attached.Info.Hwnd != d.Info.Hwnd) _events.DetachLocationAsync();
        _attached = d;
        _toolbar.AttachTo(d.Info, GetRecommended());
        _events.AttachLocation(d.Info.Hwnd);
    }

    private void DetachToolbar()
    {
        _attached = null;
        _toolbar.Detach();
        _events.DetachLocationAsync();
    }

    private void OnLocationChanged(IntPtr hwnd)
    {
        if (_attached?.Info.Hwnd == hwnd) _toolbar.Reposition();
    }

    private void OnHiddenOrEnded(IntPtr hwnd)
    {
        if (_attached?.Info.Hwnd == hwnd) DetachToolbar();
        if (_active?.Hwnd == hwnd) SetActive(null);
    }

    private void OnMinimizeEnd(IntPtr hwnd)
    {
        if (_paused || !_qs.Enabled || !_qs.ShowToolbar) return;
        if (_dialogs.TryGetValue(hwnd, out var d) && User32.GetForegroundWindow() == hwnd) AttachToolbar(d);
    }

    private void OnDestroyed(IntPtr hwnd)
    {
        if (_attached?.Info.Hwnd == hwnd) DetachToolbar();
        if (_active?.Hwnd == hwnd) SetActive(null);
        _dialogs.Remove(hwnd);
        _events.Untrack(hwnd);
    }

    private void OnDialogJump(IntPtr hwnd)
    {
        if (!_dialogs.TryGetValue(hwnd, out var d)) return;
        var target = GetRecommended();
        Logger.Debug($"Dialog jump 0x{hwnd:X} → {target}");
        if (target != null) _ = NavigateDialogAsync(d, target);
    }

    // ------------------------------------------------------------------ navigation

    private async Task NavigateFromExplorerAsync(DialogState d, IntPtr explorerHwnd)
    {
        try
        {
            var folder = _explorer.GetCached(explorerHwnd)?.Path;
            if (folder == null || !PathUtil.IsFileSystemPath(folder))
                folder = await _explorer.GetFolderAsync(explorerHwnd, CancellationToken.None);
            if (folder == null || !PathUtil.IsFileSystemPath(folder)) return;
            var current = _navigator.TryGetCurrentFolder(d.Info);
            if (PathUtil.Equal(current, folder)) return;
            await NavigateDialogAsync(d, folder);
        }
        catch (Exception ex) { Logger.Debug("Follow Explorer failed: " + ex.Message); }
    }

    private async Task<NavigateResult> NavigateDialogAsync(DialogState d, string folder)
    {
        if (!await d.Lock.WaitAsync(0)) return NavigateResult.Failed;
        try
        {
            _toolbar.SetBusy(true);
            var info = d.Info;
            var r = await Task.Run(() => _navigator.NavigateAsync(info, folder));
            _toolbar.SetBusy(false);
            switch (r)
            {
                case NavigateResult.Ok:
                    _history.RecordFolderVisit(folder, FolderVisitSource.Dialog);
                    Logger.Debug($"QuickSwitch navigated {info.ProcessName} → {folder}");
                    break;
                case NavigateResult.Blocked:
                    Logger.Debug($"QuickSwitch navigation blocked (elevated target {info.ProcessName})");
                    _toolbar.FlashError(LocalizationManager.T("QS.ElevatedHint"));
                    break;
                case NavigateResult.NotFound:
                    _toolbar.FlashError(LocalizationManager.F("QS.NavigateFailed", LocalizationManager.T("QS.NotFound")));
                    break;
                default:
                    Logger.Info($"QuickSwitch navigation {r}: {info} → {folder}");
                    _toolbar.FlashError(LocalizationManager.F("QS.NavigateFailed", r));
                    break;
            }
            return r;
        }
        finally { d.Lock.Release(); }
    }

    /// <summary>Navigate the active dialog (toolbar menus, launcher context mode).</summary>
    public Task<NavigateResult> NavigateActiveAsync(string folder) =>
        _ui.InvokeAsync(() =>
        {
            var a = _active ?? _attached?.Info;
            if (a == null || !_dialogs.TryGetValue(a.Hwnd, out var d)) return Task.FromResult(NavigateResult.Failed);
            return NavigateDialogAsync(d, folder);
        }).Task.Unwrap();

    public async Task<NavigateResult> NavigateDialogAsync(IntPtr dialog, string path, bool isFile)
    {
        var d = await _ui.InvokeAsync(() => _dialogs.TryGetValue(dialog, out var s) ? s : null);
        if (d == null) return NavigateResult.Failed;
        if (!isFile) return await NavigateDialogAsync(d, path);
        var r = await Task.Run(() => _navigator.NavigateToFileAsync(d.Info, path));
        if (r == NavigateResult.Ok) _history.RecordFolderVisit(PathUtil.GetParent(path) ?? path, FolderVisitSource.Dialog);
        return r;
    }

    // ------------------------------------------------------------------ toolbar actions (may run on the toolbar thread)

    public string? GetRecommended()
    {
        var last = _explorer.LastActive;
        if (last != null)
        {
            var p = _explorer.GetCached(last.Hwnd)?.Path;
            if (p != null && PathUtil.IsFileSystemPath(p)) return p;
        }
        var top = _explorer.Cached.FirstOrDefault(w => w.IsFileSystem);
        if (top?.Path != null) return top.Path;
        return _history.GetRecentFolders(1).FirstOrDefault()?.Path;
    }

    private string? CurrentDialogFolder()
    {
        var a = _active ?? _attached?.Info;
        return a == null ? null : _navigator.TryGetCurrentFolder(a);
    }

    public IReadOnlyList<FolderMenuItem> GetExplorerFolders()
    {
        var windows = _explorer.Cached;
        if (windows.Count == 0)
        {
            // Cold cache: one synchronous refresh with a short budget.
            try
            {
                var t = _explorer.GetWindowsAsync(CancellationToken.None);
                if (t.Wait(400)) windows = t.Result;
            }
            catch { }
        }
        else _explorer.QueueRefresh();
        return FolderMenuBuilder.Build(windows.Where(w => w.IsFileSystem).Select(w => w.Path), CurrentDialogFolder(), LocalizationManager.T("QS.NoExplorer"));
    }

    public IReadOnlyList<FolderMenuItem> GetFavorites() =>
        _settings.Current.Favorites.Select(f => new FolderMenuItem(
                string.IsNullOrEmpty(f.Alias) ? PathUtil.GetDisplayName(f.Path) : f.Alias!, f.Path, PathUtil.Equal(f.Path, CurrentDialogFolder())))
            .Where(f => f.Path != null && (PathUtil.IsNetworkPath(f.Path) || Directory.Exists(f.Path)))
            .DefaultIfEmpty(new FolderMenuItem(LocalizationManager.T("QS.NoFavorites"), null, false, true))
            .Take(FolderMenuBuilder.MaxItems).ToList();

    public IReadOnlyList<FolderMenuItem> GetRecent() =>
        FolderMenuBuilder.Build(_history.GetRecentFolders(15).Select(r => (string?)r.Path), CurrentDialogFolder(), LocalizationManager.T("QS.NoRecent"));

    public void NavigateTo(string path) => _ = NavigateActiveAsync(path);

    public void OpenSearch() => _ui.BeginInvoke(() =>
    {
        var a = _active ?? _attached?.Info;
        Logger.Debug($"Toolbar search: active={_active?.Hwnd:X} attached={_attached?.Info.Hwnd:X} handlers={SearchRequested?.GetInvocationList().Length ?? 0}");
        if (a != null) SearchRequested?.Invoke(a);
    });

    public void OpenSettings() => _ui.BeginInvoke(() => SettingsRequested?.Invoke());

    public void RestartElevated() => _ui.BeginInvoke(() => ElevationRequested?.Invoke());

    public void DisableAutoForCurrentApp() => _ui.BeginInvoke(() =>
    {
        var a = _active ?? _attached?.Info;
        if (a == null || string.IsNullOrEmpty(a.ProcessName)) return;
        _settings.Update(s =>
        {
            if (!s.QuickSwitch.AutoSwitchExcludedProcesses.Contains(a.ProcessName, StringComparer.OrdinalIgnoreCase))
                s.QuickSwitch.AutoSwitchExcludedProcesses.Add(a.ProcessName);
        });
    });

    public void DisableForCurrentApp() => _ui.BeginInvoke(() =>
    {
        var a = _active ?? _attached?.Info;
        if (a == null || string.IsNullOrEmpty(a.ProcessName)) return;
        _settings.Update(s =>
        {
            if (!s.QuickSwitch.DisabledProcesses.Contains(a.ProcessName, StringComparer.OrdinalIgnoreCase))
                s.QuickSwitch.DisabledProcesses.Add(a.ProcessName);
        });
        DetachToolbar();
        SetActive(null);
    });

    public void AddCurrentToFavorites() => _ui.BeginInvoke(() =>
    {
        var folder = CurrentDialogFolder();
        if (folder == null) return;
        _settings.Update(s =>
        {
            if (!s.Favorites.Any(f => PathUtil.Equal(f.Path, folder))) s.Favorites.Add(new FavoriteItem(folder));
        });
    });

    public void Dispose()
    {
        _events.Foreground -= OnForeground;
        _toolbar.Dispose();
    }
}

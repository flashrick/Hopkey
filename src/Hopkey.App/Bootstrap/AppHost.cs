using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Hopkey.App.Explorer;
using Hopkey.App.Hooks;
using Hopkey.App.Infrastructure;
using Hopkey.App.Launcher;
using Hopkey.App.Onboarding;
using Hopkey.App.QuickSwitch;
using Hopkey.App.Settings;
using Hopkey.App.Tray;
using Hopkey.Core.Config;
using Hopkey.Core.Exclusion;
using Hopkey.Core.History;
using Hopkey.Core.Indexing;
using Hopkey.Core.Native;
using Hopkey.Core.Pinyin;
using Hopkey.Core.Search;
using Hopkey.Core.Search.Providers;
using Hopkey.Core.Util;
using Microsoft.Win32;

namespace Hopkey.App.Bootstrap;

/// <summary>
/// Composition root. Synchronous startup does only: settings → theme/strings → tray → hooks (target &lt;400ms to tray).
/// Everything else (index load, app catalog, pinyin, launcher pre-creation, Explorer cache) runs at idle / in background.
/// </summary>
public sealed class AppHost
{
    public static AppHost? Instance { get; set; }

    private readonly Application _app;
    private readonly StartupOptions _options;
    private readonly Dispatcher _ui;
    private SettingsStore _settings = null!;
    private HistoryStore _history = null!;
    private TrayIconService _tray = null!;
    private DispatcherThread _hookThread = null!;
    private WinEventHub _winEvents = null!;
    private KeyboardHook _keyboard = null!;
    private HotkeyService _hotkeys = null!;
    private ExplorerService _explorer = null!;
    private QuickSwitchService _quickSwitch = null!;
    private TypeToSearchService _typeToSearch = null!;
    private IndexManager? _index;
    private AppCatalog? _apps;
    private SearchEngine? _engine;
    private IconCache? _icons;
    private LauncherViewModel? _launcherVm;
    private LauncherWindow? _launcher;
    private SettingsWindow? _settingsWindow;
    private SettingsViewModel? _settingsVm;
    private OnboardingWindow? _onboarding;
    private UpdateService _updates = null!;
    private bool _paused;
    private bool _shutdown;

    public AppHost(Application app, StartupOptions options)
    {
        _app = app;
        _options = options;
        _ui = app.Dispatcher;
        Instance = this;
    }

    public void Start()
    {
        var t0 = PerfLog.Start();
        _settings = SettingsStore.Load();
        var s = _settings.Current;
        LocalizationManager.Apply(s.General.Language);
        ThemeManager.Initialize(s.General.Theme);

        _tray = new TrayIconService(ElevationHelper.IsSelfElevated);
        _tray.SearchRequested += () => ShowLauncher();
        _tray.SettingsRequested += () => ShowSettings();
        _tray.RebuildRequested += () => _ = _index?.RebuildAsync();
        _tray.PauseToggled += TogglePause;
        _tray.ElevateRequested += RestartElevated;
        _tray.ExitRequested += Exit;
        _tray.GuideRequested += () => ShowOnboarding(auto: false);
        _updates = new UpdateService(_ui, _settings, _tray);
        _updates.StatusChanged += () => { if (_settingsVm != null) _settingsVm.UpdateStatus = _updates.Status; };

        _history = HistoryStore.Load();

        _hookThread = new DispatcherThread("CL-Hooks", ThreadPriority.Highest);
        _winEvents = new WinEventHub(_hookThread, _ui);
        // Backstop for click-away: WPF's Deactivated isn't always raised (the launcher could stay up after the user
        // clicked another app). The hook skips our own process, so this only fires for other apps' windows.
        _winEvents.Foreground += hwnd => _launcher?.OnExternalForeground(hwnd);
        _keyboard = new KeyboardHook(_hookThread);
        _hotkeys = new HotkeyService(_keyboard, _ui);
        _hotkeys.LauncherRequested += () => { ToggleLauncher(); _onboarding?.NotifyLauncherHotkey(); };
        _hotkeys.RegistrationFailed += spec => _tray.ShowBalloon(LocalizationManager.F("Tray.HotkeyConflict", spec), true);
        _explorer = new ExplorerService(_history);
        _quickSwitch = new QuickSwitchService(_ui, _winEvents, _explorer, _hotkeys, _history, _settings);
        _quickSwitch.SearchRequested += dlg => ShowLauncherContext(new LauncherContext(LauncherContextKind.Dialog, dlg.Hwnd, dlg, User32.GetWindowText(dlg.Hwnd)), null);
        _quickSwitch.SettingsRequested += () => ShowSettings(1);
        _quickSwitch.ElevationRequested += RestartElevated;
        _typeToSearch = new TypeToSearchService(_keyboard, _ui);
        _quickSwitch.ActiveDialogChanged += d => _typeToSearch.ActiveDialog = d?.Hwnd ?? IntPtr.Zero;
        _typeToSearch.Triggered += OnTypeToSearch;

        _winEvents.Start();
        _keyboard.Install();
        ApplyHotkeysAndQuickSwitch(s);
        _ui.BeginInvoke(DispatcherPriority.Background, _quickSwitch.CheckCurrentForeground);
        // Spawns schtasks.exe: keep it off the startup path.
        Task.Run(() => AutostartService.Apply(s.General, userChange: false));
        _settings.Changed += (o, n) =>
        {
            if (_ui.CheckAccess()) OnSettingsChanged(o, n);
            else _ui.BeginInvoke(() => OnSettingsChanged(o, n));
        };
        SystemEvents.PowerModeChanged += (_, e) => { if (e.Mode == PowerModes.Resume) _ui.BeginInvoke(ReinstallHooks); };
        SystemEvents.SessionSwitch += (_, e) => { if (e.Reason == SessionSwitchReason.SessionUnlock) _ui.BeginInvoke(ReinstallHooks); };
        PerfLog.Stop("startup.tray", t0);

        _ui.BeginInvoke(DispatcherPriority.ApplicationIdle, StartDeferred);
        _ui.BeginInvoke(DispatcherPriority.ApplicationIdle, () => _updates.Start(_options.Update));
        if (_options.ElevatedRestart) _ui.BeginInvoke(DispatcherPriority.ApplicationIdle, () => ShowSettings());
        else if (OnboardingPolicy.ShouldAutoShow(s.Onboarding!, _options.Autostart, _options.ElevatedRestart, _options.ForceOnboarding))
            _ui.BeginInvoke(DispatcherPriority.ApplicationIdle, () => ShowOnboarding(auto: true));
        if (_settings.IsFirstRun) Logger.Info("First run (no settings.json / history.json)");
    }

    /// <summary>Deferred startup: indexing, search, launcher pre-creation, caches.</summary>
    private void StartDeferred()
    {
        try
        {
            Task.Run(PinyinInitialsTable.Initialize);
            EnsureSearch();
            EnsureLauncher();
            _launcher!.Prewarm();
            _ = _index!.StartAsync();
            if (_settings.Current.Index.IndexApps) Task.Run(() => _apps!.Start(_settings.Current.Index.IndexUwpApps));
            _explorer.QueueRefresh();
        }
        catch (Exception ex) { Logger.Error("Deferred startup failed", ex); }
    }

    private void EnsureSearch()
    {
        if (_engine != null) return;
        var s = _settings.Current;
        _index = new IndexManager(() => _settings.Current.Index, ExclusionMatcher.Create(s.Exclusion));
        _index.StatusChanged += st => _ui.BeginInvoke(() =>
        {
            _launcherVm?.UpdateIndexStatus(st);
            if (_settingsVm != null) _settingsVm.IndexStatusText = FormatIndexStats();
        });
        // Live entry count (prunes, live updates) between status transitions.
        _index.Index.Changed += () => _ui.BeginInvoke(() =>
        {
            var st = _index.Status;
            _launcherVm?.UpdateIndexStatus(st with { Count = _index.Index.TotalLiveCount });
        });
        _apps = new AppCatalog();
        var providers = new List<ISearchProvider>
        {
            new CalculatorProvider(),
            new WebSearchProvider(),
            new AppSearchProvider(_apps),
            new HistorySearchProvider(_history, () => _settings.Current),
            new IndexSearchProvider(_index.Index, () => _settings.Current.Search),
        };
        _engine = new SearchEngine(providers, _history, () => _index.Matcher, () => _settings.Current);
    }

    private void EnsureLauncher()
    {
        if (_launcher != null) return;
        EnsureSearch();
        _icons = new IconCache();
        _launcherVm = new LauncherViewModel(_engine!, _icons, _history, _settings, _ui);
        _launcherVm.UpdateIndexStatus(_index!.Status);
        var exec = new ActionExecutor(_history, _settings, p => _index!.Index.RemovePath(p));
        _launcher = new LauncherWindow(_launcherVm, exec, _icons, NavigateContextAsync, () => SettingsWindow.OpenFolder(AppPaths.LogDirectory));
    }

    private string FormatIndexStats()
    {
        if (_index == null) return "";
        var lines = _index.GetStats().Select(v =>
            $"{v.Root}  [{(v.Kind == IndexSourceKind.Usn ? "MFT/USN" : "Crawler")}]  {v.LiveCount:N0}  {v.LastUpdatedUtc.ToLocalTime():g}" +
            (v.LastBuildTime is { } t ? $"  ({t.TotalSeconds:F1}s)" : ""));
        return $"{_index.Status.State} · {_index.Index.TotalLiveCount:N0}\n" + string.Join("\n", lines);
    }

    // ------------------------------------------------------------------ launcher

    public void ShowLauncher() => ShowLauncherContext(LauncherContext.Normal, null);

    private void ToggleLauncher()
    {
        if (_paused) return;
        EnsureLauncher();
        _launcher!.Toggle(LauncherContext.Normal);
    }

    private void ShowLauncherContext(LauncherContext ctx, string? text)
    {
        EnsureLauncher();
        _launcher!.ShowLauncher(ctx, text);
    }

    private void OnTypeToSearch(IntPtr fg, string ch)
    {
        if (_paused) return;
        LauncherContext ctx;
        var dlg = _quickSwitch.ActiveDialog;
        if (dlg != null && dlg.Hwnd == fg) ctx = new LauncherContext(LauncherContextKind.Dialog, fg, dlg, User32.GetWindowText(fg));
        else ctx = new LauncherContext(LauncherContextKind.Explorer, fg, null, User32.GetWindowText(fg));
        ShowLauncherContext(ctx, ch);
    }

    private async Task<bool> NavigateContextAsync(LauncherContext ctx, SearchResult r, bool isFile)
    {
        if (r.Path == null) return false;
        try
        {
            switch (ctx.Kind)
            {
                case LauncherContextKind.Explorer:
                {
                    ForegroundHelper.Activate(ctx.Hwnd);
                    bool ok = isFile
                        ? await _explorer.NavigateAndSelectAsync(ctx.Hwnd, r.Path, CancellationToken.None)
                        : await _explorer.NavigateAsync(ctx.Hwnd, r.Path, CancellationToken.None);
                    if (ok && !isFile) _history.RecordFolderVisit(r.Path, FolderVisitSource.Explorer);
                    return ok;
                }
                case LauncherContextKind.Dialog:
                {
                    ForegroundHelper.Activate(ctx.Hwnd);
                    var res = await _quickSwitch.NavigateDialogAsync(ctx.Hwnd, r.Path, isFile);
                    return res == NavigateResult.Ok;
                }
            }
        }
        catch (Exception ex) { Logger.Warn("Context navigation failed", ex); }
        return false;
    }

    // ------------------------------------------------------------------ settings

    private void ShowSettings(int tab = 0)
    {
        if (_settingsWindow != null)
        {
            _settingsWindow.SelectTab(tab);
            if (_settingsWindow.WindowState == WindowState.Minimized) _settingsWindow.WindowState = WindowState.Normal;
            _settingsWindow.Activate();
            return;
        }
        EnsureSearch();
        _settingsVm = new SettingsViewModel(_settings) { ProbeHotkey = spec => _hotkeys.TryProbe(spec) };
        _settingsVm.IndexStatusText = FormatIndexStats();
        _settingsVm.RebuildRequested += () => _ = _index?.RebuildAsync();
        _settingsVm.RestartElevatedRequested += RestartElevated;
        _settingsVm.ClearHistoryRequested += () => _history.ClearAll();
        _settingsVm.GuideRequested += () => ShowOnboarding(auto: false);
        _settingsVm.CheckUpdatesRequested += () => _ = _updates.CheckAsync(manual: true);
        _settingsVm.UpdateStatus = _updates.Status;
        _settingsVm.UserData = new UserDataService(_settings, _history);
        _settingsWindow = new SettingsWindow(_settingsVm);
        _settingsWindow.Closed += (_, _) => { _settingsWindow = null; _settingsVm = null; };
        _settingsWindow.SelectTab(tab);
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    // ------------------------------------------------------------------ onboarding

    private void ShowOnboarding(bool auto)
    {
        if (_shutdown) return;
        if (_onboarding != null) { _onboarding.ActivateWindow(); return; }
        var t0 = PerfLog.Start();
        var vm = new OnboardingViewModel(_settings, isFirstRun: auto && _settings.IsFirstRun, isPaused: () => _paused);
        vm.OpenTaskbarSettingsRequested += OpenTaskbarSettings;
        var w = _onboarding = new OnboardingWindow(vm);
        w.Closed += (_, _) => { if (_onboarding == w) _onboarding = null; OnOnboardingClosed(); };
        w.Show();
        w.ActivateWindow();
        // Seen as soon as it pops: closing it mid-way means "not now", and it stays reachable from the tray menu.
        if (auto && _settings.Current.Onboarding!.SeenVersion < OnboardingState.CurrentVersion)
            _settings.Update(x => x.Onboarding!.SeenVersion = OnboardingState.CurrentVersion);
        PerfLog.Stop("onboarding.show", t0);
    }

    private void OnOnboardingClosed()
    {
        if (_shutdown || _app.Dispatcher.HasShutdownStarted) return; // tray Exit while the guide is open: no balloon
        if (_settings.Current.Onboarding!.TrayBalloonShown) return;
        if (TrayPromotionProbe.Read() != TrayPromotion.Promoted)
        {
            var launcher = _settings.Current.Hotkeys.Launcher;
            _tray.ShowBalloon(HotkeySpec.Parse(launcher).IsDoubleCtrl
                ? LocalizationManager.T("Onboarding.Balloon")
                : LocalizationManager.F("Onboarding.BalloonCombo", launcher));
        }
        _settings.Update(x => x.Onboarding!.TrayBalloonShown = true);
    }

    /// <summary>The exe was launched again while running: bring the guide forward if it's open, else the launcher.</summary>
    public void OnSecondInstance()
    {
        if (_onboarding != null) _onboarding.ActivateWindow();
        else ShowLauncher();
    }

    private static void OpenTaskbarSettings()
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:taskbar") { UseShellExecute = true }); }
        catch (Exception ex) { Logger.Warn("Opening taskbar settings failed", ex); }
    }

    private static bool SectionEqual<T>(T a, T b) => JsonSerializer.Serialize(a) == JsonSerializer.Serialize(b);

    private void OnSettingsChanged(AppSettings o, AppSettings n)
    {
        try
        {
            if (o.General.Language != n.General.Language)
            {
                LocalizationManager.Apply(n.General.Language);
                _tray.UpdateTexts(_paused);
                _launcherVm?.RefreshLocalization();
                _quickSwitch.RefreshResources();
            }
            if (o.General.Theme != n.General.Theme)
            {
                ThemeManager.Apply(n.General.Theme);
                _quickSwitch.RefreshResources();
            }
            if (o.General.Autostart != n.General.Autostart || o.General.AutostartElevated != n.General.AutostartElevated)
                Task.Run(() => AutostartService.Apply(n.General, userChange: true));
            if (!SectionEqual(o.Hotkeys, n.Hotkeys) || !SectionEqual(o.QuickSwitch, n.QuickSwitch))
                ApplyHotkeysAndQuickSwitch(n);
            if (_index != null)
            {
                if (!SectionEqual(o.Exclusion, n.Exclusion))
                    _index.OnExclusionChanged(ExclusionMatcher.Create(n.Exclusion));
                if (!SectionEqual(o.Index.Roots, n.Index.Roots)) _index.OnRootsChanged();
                else if (o.Index.UseUsnWhenElevated != n.Index.UseUsnWhenElevated) _index.OnSourceModeChanged();
            }
            if (_apps != null)
            {
                if (n.Index.IndexApps && !o.Index.IndexApps) Task.Run(() => _apps.Start(n.Index.IndexUwpApps));
                else if (o.Index.IndexUwpApps != n.Index.IndexUwpApps) _apps.SetIncludeUwp(n.Index.IndexUwpApps);
            }
        }
        catch (Exception ex) { Logger.Error("Applying settings failed", ex); }
    }

    private void ApplyHotkeysAndQuickSwitch(AppSettings s)
    {
        _hotkeys.Apply(s.Hotkeys, s.QuickSwitch, _paused);
        _quickSwitch.Apply(s.QuickSwitch, _paused);
        _typeToSearch.Apply(!_paused && s.Hotkeys.TypeToSearch);
    }

    private void ReinstallHooks()
    {
        if (_shutdown) return;
        Logger.Info("Reinstalling hooks (resume/unlock)");
        _keyboard.Reinstall();
        _winEvents.Stop();
        _winEvents.Start();
        ApplyHotkeysAndQuickSwitch(_settings.Current);
    }

    private void TogglePause()
    {
        _paused = !_paused;
        ApplyHotkeysAndQuickSwitch(_settings.Current);
        _index?.Pause(_paused);
        _tray.UpdateTexts(_paused);
        _onboarding?.ViewModel.RefreshPaused();
        if (!_paused)
        {
            ReinstallHooks();
            _quickSwitch.CheckCurrentForeground();
        }
    }

    private void RestartElevated()
    {
        if (ElevationHelper.IsSelfElevated) return;
        ElevationService.RestartElevated(() => { });
    }

    private void Exit() => _app.Shutdown();

    public void Shutdown()
    {
        if (_shutdown) return;
        _shutdown = true;
        Logger.Info("Shutting down");
        try { _keyboard.Uninstall(); } catch { }
        try { _winEvents.Stop(); } catch { }
        try { _hotkeys.Dispose(); } catch { }
        try { _typeToSearch.Dispose(); } catch { }
        try { _quickSwitch.Dispose(); } catch { }
        try
        {
            var idx = _index;
            if (idx != null)
            {
                var t = Task.Run(idx.ShutdownAndSave);
                if (!t.Wait(5000)) Logger.Warn("Index save timed out at shutdown");
                idx.Dispose();
            }
        }
        catch (Exception ex) { Logger.Warn("Index shutdown failed", ex); }
        try { _history.Dispose(); } catch { }
        try { _apps?.Dispose(); } catch { }
        try { _explorer.Dispose(); } catch { }
        try { _icons?.Dispose(); } catch { }
        try { _updates.Dispose(); } catch { }
        try { _tray.Dispose(); } catch { }
        try { _hookThread.Dispose(); } catch { }
    }
}

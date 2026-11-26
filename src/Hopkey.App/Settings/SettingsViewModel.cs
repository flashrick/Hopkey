using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Threading;
using Hopkey.App.Infrastructure;
using Hopkey.App.Mvvm;
using Hopkey.Core.Config;
using Hopkey.Core.Exclusion;
using Hopkey.Core.Native;

namespace Hopkey.App.Settings;

public sealed record Choice<T>(T Value, string Display);

public sealed class ExclusionRuleViewModel : ObservableObject
{
    private readonly ExclusionRule _rule;
    private readonly Action _changed;
    public ExclusionRuleViewModel(ExclusionRule rule, Action changed) { _rule = rule; _changed = changed; }
    public ExclusionRule Rule => _rule;
    public bool Enabled { get => _rule.Enabled; set { _rule.Enabled = value; Changed(); } }
    public ExclusionKind Kind { get => _rule.Kind; set { if (_rule.BuiltIn) return; _rule.Kind = value; Changed(); } }
    public string Pattern { get => _rule.Pattern; set { if (_rule.BuiltIn) return; _rule.Pattern = value ?? ""; Changed(); } }
    public ExclusionTarget Target { get => _rule.Target; set { _rule.Target = value; Changed(); } }
    public bool BuiltIn => _rule.BuiltIn;
    public bool IsEditable => !_rule.BuiltIn;
    public string BuiltInText => _rule.BuiltIn ? "✓" : "";
    public string? Error => _rule.Validate();
    public bool HasError => Error != null;
    private void Changed()
    {
        OnPropertyChanged(null);
        _changed();
    }
}

public sealed class FavoriteViewModel : ObservableObject
{
    private readonly FavoriteItem _item;
    private readonly Action _changed;
    public FavoriteViewModel(FavoriteItem item, Action changed) { _item = item; _changed = changed; }
    public FavoriteItem Item => _item;
    public string Path { get => _item.Path; set { _item.Path = value ?? ""; OnPropertyChanged(); _changed(); } }
    public string? Alias { get => _item.Alias; set { _item.Alias = string.IsNullOrWhiteSpace(value) ? null : value; OnPropertyChanged(); _changed(); } }
}

public sealed class WebEngineViewModel : ObservableObject
{
    private readonly WebSearchEngine _e;
    private readonly Action _changed;
    public WebEngineViewModel(WebSearchEngine e, Action changed) { _e = e; _changed = changed; }
    public WebSearchEngine Engine => _e;
    public bool Enabled { get => _e.Enabled; set { _e.Enabled = value; OnPropertyChanged(); _changed(); } }
    public string Keyword { get => _e.Keyword; set { _e.Keyword = (value ?? "").Trim(); OnPropertyChanged(); _changed(); } }
    public string Name { get => _e.Name; set { _e.Name = value ?? ""; OnPropertyChanged(); _changed(); } }
    public string UrlTemplate { get => _e.UrlTemplate; set { _e.UrlTemplate = value ?? ""; OnPropertyChanged(); _changed(); } }
}

/// <summary>
/// Settings window VM over a draft copy of AppSettings. Every change is applied live (300ms debounce) via
/// SettingsStore.Update. External changes (favorites from the launcher, exclusions from the action menu) reload the draft.
/// </summary>
public sealed class SettingsViewModel : ObservableObject, IDisposable
{
    private readonly SettingsStore _store;
    private readonly DispatcherTimer _saveTimer;
    private AppSettings _draft;
    private bool _saving;
    private bool _loading;
    private string _savedText = "";

    public ObservableCollection<ExclusionRuleViewModel> Rules { get; } = new();
    public ObservableCollection<FavoriteViewModel> Favorites { get; } = new();
    public ObservableCollection<WebEngineViewModel> WebEngines { get; } = new();
    public ObservableCollection<string> Roots { get; } = new();

    public List<Choice<string>> Languages { get; } = new() { new("zh-CN", "简体中文"), new("en", "English") };
    public List<Choice<ThemeMode>> Themes => new()
    {
        new(ThemeMode.System, LocalizationManager.T("Settings.Theme.System")),
        new(ThemeMode.Light, LocalizationManager.T("Settings.Theme.Light")),
        new(ThemeMode.Dark, LocalizationManager.T("Settings.Theme.Dark")),
    };
    public List<Choice<ExclusionKind>> Kinds => Enum.GetValues<ExclusionKind>().Select(k => new Choice<ExclusionKind>(k, LocalizationManager.T("Kind." + k))).ToList();
    public List<Choice<ExclusionTarget>> Targets => Enum.GetValues<ExclusionTarget>().Select(k => new Choice<ExclusionTarget>(k, LocalizationManager.T("Target." + k))).ToList();

    public event Action? RebuildRequested;
    public event Action? RestartElevatedRequested;
    public event Action? ClearHistoryRequested;
    public Func<Core.Config.HotkeySpec, bool>? ProbeHotkey { get; set; }

    public SettingsViewModel(SettingsStore store)
    {
        _store = store;
        _draft = store.Current.Clone();
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); SaveNow(); };
        _store.Changed += OnStoreChanged;
        LoadCollections();
    }

    private void OnStoreChanged(AppSettings oldS, AppSettings newS)
    {
        if (_saving) return;
        System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
        {
            if (_saveTimer.IsEnabled) return; // our pending edit wins
            _draft = _store.Current.Clone();
            LoadCollections();
            OnPropertyChanged(null);
        });
    }

    private void LoadCollections()
    {
        _loading = true;
        Rules.Clear();
        foreach (var r in _draft.Exclusion.Rules) Rules.Add(new ExclusionRuleViewModel(r, Schedule));
        Favorites.Clear();
        foreach (var f in _draft.Favorites) Favorites.Add(new FavoriteViewModel(f, Schedule));
        WebEngines.Clear();
        foreach (var w in _draft.WebSearch) WebEngines.Add(new WebEngineViewModel(w, Schedule));
        Roots.Clear();
        foreach (var r in _draft.Index.Roots) Roots.Add(r);
        _loading = false;
    }

    public void Schedule()
    {
        if (_loading) return;
        _saveTimer.Stop();
        _saveTimer.Start();
        OnPropertyChanged(nameof(RuleErrors));
        OnPropertyChanged(nameof(WebError));
    }

    public void SaveNow()
    {
        _saveTimer.Stop();
        _draft.Exclusion.Rules = Rules.Select(r => r.Rule).ToList();
        _draft.Favorites = Favorites.Select(f => f.Item).Where(f => !string.IsNullOrWhiteSpace(f.Path)).ToList();
        _draft.WebSearch = WebEngines.Select(w => w.Engine).ToList();
        _draft.Index.Roots = Roots.ToList();
        var copy = _draft.Clone();
        _saving = true;
        try
        {
            _store.Update(s =>
            {
                s.General = copy.General;
                s.Hotkeys = copy.Hotkeys;
                s.QuickSwitch = copy.QuickSwitch;
                s.Index = copy.Index;
                s.Exclusion = copy.Exclusion;
                s.Search = copy.Search;
                s.Favorites = copy.Favorites;
                s.WebSearch = ValidWeb(copy.WebSearch) ? copy.WebSearch : s.WebSearch;
            });
        }
        finally { _saving = false; }
        ShowSaved();
    }

    private async void ShowSaved()
    {
        SavedText = LocalizationManager.T("Saved");
        await Task.Delay(1500);
        SavedText = "";
    }

    public string SavedText { get => _savedText; private set => Set(ref _savedText, value); }

    // ---------------- General
    public bool Autostart { get => _draft.General.Autostart; set { _draft.General.Autostart = value; OnPropertyChanged(); Schedule(); } }
    public bool AutostartElevated { get => _draft.General.AutostartElevated; set { _draft.General.AutostartElevated = value; OnPropertyChanged(); Schedule(); } }
    public bool CanAutostartElevated => ElevationHelper.IsSelfElevated;
    public string Language
    {
        get => _draft.General.Language;
        set { if (string.IsNullOrEmpty(value)) return; _draft.General.Language = value; OnPropertyChanged(); Schedule(); }
    }
    public ThemeMode Theme { get => _draft.General.Theme; set { _draft.General.Theme = value; OnPropertyChanged(); Schedule(); } }

    public bool LauncherDoubleCtrl
    {
        get => HotkeySpec.Parse(_draft.Hotkeys.Launcher).IsDoubleCtrl;
        set
        {
            if (value == LauncherDoubleCtrl) return; // radio-group feedback must not re-notify (re-entrancy loop)
            if (value) _draft.Hotkeys.Launcher = HotkeySpec.DoubleCtrl;
            else if (HotkeySpec.Parse(_draft.Hotkeys.Launcher).IsDoubleCtrl) _draft.Hotkeys.Launcher = "Alt+Space";
            OnPropertyChanged();
            OnPropertyChanged(nameof(LauncherCombination));
            OnPropertyChanged(nameof(LauncherCombo));
            Schedule();
        }
    }

    public bool LauncherCombination { get => !LauncherDoubleCtrl; set => LauncherDoubleCtrl = !value; }

    public string LauncherCombo
    {
        get => LauncherDoubleCtrl ? "" : _draft.Hotkeys.Launcher;
        set
        {
            if (LauncherDoubleCtrl) return;
            if (!ValidateHotkey(value)) { System.Windows.Application.Current.Dispatcher.BeginInvoke(() => OnPropertyChanged(nameof(LauncherCombo))); System.Windows.Application.Current.Dispatcher.BeginInvoke(() => OnPropertyChanged(nameof(LauncherAlt))); return; }
            _draft.Hotkeys.Launcher = value;
            OnPropertyChanged();
            Schedule();
        }
    }

    public string LauncherAlt
    {
        get => _draft.Hotkeys.LauncherAlt;
        set { if (!ValidateHotkey(value)) { System.Windows.Application.Current.Dispatcher.BeginInvoke(() => OnPropertyChanged(nameof(LauncherCombo))); System.Windows.Application.Current.Dispatcher.BeginInvoke(() => OnPropertyChanged(nameof(LauncherAlt))); return; } _draft.Hotkeys.LauncherAlt = value ?? ""; OnPropertyChanged(); Schedule(); }
    }

    public string DialogJump
    {
        get => _draft.Hotkeys.DialogJump;
        set { var spec = HotkeySpec.Parse(value); if (!string.IsNullOrEmpty(value) && !spec.IsValid) return; _draft.Hotkeys.DialogJump = value ?? ""; OnPropertyChanged(); Schedule(); }
    }

    private string _hotkeyError = "";
    public string HotkeyError { get => _hotkeyError; private set => Set(ref _hotkeyError, value); }

    private bool ValidateHotkey(string? value)
    {
        if (string.IsNullOrEmpty(value)) { HotkeyError = ""; return true; }
        var spec = HotkeySpec.Parse(value);
        // Already registered by us -> probing fails; only probe when it differs from the current registrations.
        bool ours = value == _store.Current.Hotkeys.Launcher || value == _store.Current.Hotkeys.LauncherAlt;
        bool ok = spec.IsValid && (ours || ProbeHotkey == null || ProbeHotkey(spec));
        HotkeyError = ok ? "" : LocalizationManager.T("Settings.HotkeyInvalid") + ": " + value;
        return ok;
    }

    public bool TypeToSearch { get => _draft.Hotkeys.TypeToSearch; set { _draft.Hotkeys.TypeToSearch = value; OnPropertyChanged(); Schedule(); } }
    public bool IsElevated => ElevationHelper.IsSelfElevated;
    public string PrivilegeText => IsElevated ? LocalizationManager.T("Settings.Elevated") : LocalizationManager.T("Settings.NotElevated");
    public void RequestRestartElevated() => RestartElevatedRequested?.Invoke();

    // ---------------- Search
    public bool Pinyin { get => _draft.Search.Pinyin; set { _draft.Search.Pinyin = value; OnPropertyChanged(); Schedule(); } }
    public bool HistoryBoost { get => _draft.Search.UseHistoryBoost; set { _draft.Search.UseHistoryBoost = value; OnPropertyChanged(); Schedule(); } }
    public int MaxResults { get => _draft.Search.MaxResults; set { _draft.Search.MaxResults = Math.Clamp(value, 5, 500); OnPropertyChanged(); Schedule(); } }
    public bool ShowCreated { get => _draft.Search.ShowCreated; set { _draft.Search.ShowCreated = value; OnPropertyChanged(); Schedule(); } }
    public bool ShowModified { get => _draft.Search.ShowModified; set { _draft.Search.ShowModified = value; OnPropertyChanged(); Schedule(); } }
    public bool ShowSize { get => _draft.Search.ShowSize; set { _draft.Search.ShowSize = value; OnPropertyChanged(); Schedule(); } }
    public List<Choice<ResultSortMode>> SortModes => Enum.GetValues<ResultSortMode>()
        .Select(m => new Choice<ResultSortMode>(m, LocalizationManager.T("Sort." + m))).ToList();
    public ResultSortMode SortBy { get => _draft.Search.SortBy; set { _draft.Search.SortBy = value; OnPropertyChanged(); Schedule(); } }
    public void ClearHistory() => ClearHistoryRequested?.Invoke();

    // ---------------- QuickSwitch
    public bool QsEnabled { get => _draft.QuickSwitch.Enabled; set { _draft.QuickSwitch.Enabled = value; OnPropertyChanged(); Schedule(); } }
    public bool QsShowToolbar { get => _draft.QuickSwitch.ShowToolbar; set { _draft.QuickSwitch.ShowToolbar = value; OnPropertyChanged(); Schedule(); } }
    public bool QsAutoOnOpen { get => _draft.QuickSwitch.AutoSwitchOnOpen; set { _draft.QuickSwitch.AutoSwitchOnOpen = value; OnPropertyChanged(); Schedule(); } }
    public int QsMaxAge { get => _draft.QuickSwitch.AutoSwitchMaxAgeMinutes; set { _draft.QuickSwitch.AutoSwitchMaxAgeMinutes = Math.Clamp(value, 1, 1440); OnPropertyChanged(); Schedule(); } }
    public bool QsFollow { get => _draft.QuickSwitch.FollowExplorer; set { _draft.QuickSwitch.FollowExplorer = value; OnPropertyChanged(); Schedule(); } }
    public bool QsDialogJump { get => _draft.QuickSwitch.DialogJumpHotkey; set { _draft.QuickSwitch.DialogJumpHotkey = value; OnPropertyChanged(); Schedule(); } }

    public string QsDisabledApps
    {
        get => string.Join(Environment.NewLine, _draft.QuickSwitch.DisabledProcesses);
        set { _draft.QuickSwitch.DisabledProcesses = SplitLines(value); Schedule(); }
    }

    public string QsAutoExcludedApps
    {
        get => string.Join(Environment.NewLine, _draft.QuickSwitch.AutoSwitchExcludedProcesses);
        set { _draft.QuickSwitch.AutoSwitchExcludedProcesses = SplitLines(value); Schedule(); }
    }

    private static List<string> SplitLines(string? s) =>
        (s ?? "").Split(new[] { '\r', '\n', ';', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    // ---------------- Index
    public bool AllDrives
    {
        get => Roots.Count == 0;
        set
        {
            if (value) Roots.Clear();
            OnPropertyChanged();
            Schedule();
        }
    }

    public void AddRoot(string path)
    {
        if (Roots.Any(r => Core.Util.PathUtil.Equal(r, path))) return;
        Roots.Add(path);
        OnPropertyChanged(nameof(AllDrives));
        Schedule();
    }

    public void RemoveRoot(string path)
    {
        Roots.Remove(path);
        OnPropertyChanged(nameof(AllDrives));
        Schedule();
    }

    public bool UseUsn { get => _draft.Index.UseUsnWhenElevated; set { _draft.Index.UseUsnWhenElevated = value; OnPropertyChanged(); Schedule(); } }
    public bool IndexApps { get => _draft.Index.IndexApps; set { _draft.Index.IndexApps = value; OnPropertyChanged(); Schedule(); } }
    public bool IndexUwp { get => _draft.Index.IndexUwpApps; set { _draft.Index.IndexUwpApps = value; OnPropertyChanged(); Schedule(); } }
    public bool ExcludeHidden { get => _draft.Exclusion.ExcludeHidden; set { _draft.Exclusion.ExcludeHidden = value; OnPropertyChanged(); Schedule(); } }
    public bool ExcludeSystem { get => _draft.Exclusion.ExcludeSystem; set { _draft.Exclusion.ExcludeSystem = value; OnPropertyChanged(); Schedule(); } }

    private string _indexStatusText = "";
    public string IndexStatusText { get => _indexStatusText; set => Set(ref _indexStatusText, value); }
    public void Rebuild() => RebuildRequested?.Invoke();

    public string RuleErrors => string.Join("\n", Rules.Where(r => r.HasError).Select(r => $"{r.Pattern}: {r.Error}"));

    public void AddRule(ExclusionKind kind, string pattern)
    {
        var target = kind == ExclusionKind.Name && pattern.StartsWith("*.", StringComparison.Ordinal) ? ExclusionTarget.FilesOnly
            : kind is ExclusionKind.Path or ExclusionKind.Glob or ExclusionKind.Regex ? ExclusionTarget.DirectoriesOnly : ExclusionTarget.Both;
        var rule = new ExclusionRule(kind, pattern, target);
        Rules.Add(new ExclusionRuleViewModel(rule, Schedule));
        Schedule();
    }

    public bool RemoveRule(ExclusionRuleViewModel vm)
    {
        if (vm.BuiltIn) return false;
        Rules.Remove(vm);
        Schedule();
        return true;
    }

    public void ImportRules(IEnumerable<ExclusionRule> rules)
    {
        foreach (var r in rules)
        {
            if (Rules.Any(x => x.Kind == r.Kind && string.Equals(x.Pattern, r.Pattern, StringComparison.OrdinalIgnoreCase))) continue;
            Rules.Add(new ExclusionRuleViewModel(r, Schedule));
        }
        Schedule();
    }

    public void RestoreDefaultRules()
    {
        Rules.Clear();
        foreach (var r in DefaultExclusions.Create()) Rules.Add(new ExclusionRuleViewModel(r, Schedule));
        _draft.Exclusion.ExcludeHidden = false;
        _draft.Exclusion.ExcludeSystem = true;
        OnPropertyChanged(nameof(ExcludeHidden));
        OnPropertyChanged(nameof(ExcludeSystem));
        Schedule();
    }

    // ---------------- Favorites
    public void AddFavorite(string path)
    {
        if (Favorites.Any(f => Core.Util.PathUtil.Equal(f.Path, path))) return;
        Favorites.Add(new FavoriteViewModel(new FavoriteItem(path), Schedule));
        Schedule();
    }

    public void RemoveFavorite(FavoriteViewModel f) { Favorites.Remove(f); Schedule(); }

    public void MoveFavorite(FavoriteViewModel f, int delta)
    {
        int i = Favorites.IndexOf(f);
        int j = i + delta;
        if (i < 0 || j < 0 || j >= Favorites.Count) return;
        Favorites.Move(i, j);
        Schedule();
    }

    // ---------------- Web
    public void AddWebEngine()
    {
        WebEngines.Add(new WebEngineViewModel(new WebSearchEngine("new", "New", "https://example.com/search?q={q}"), Schedule));
        Schedule();
    }

    public void RemoveWebEngine(WebEngineViewModel w) { WebEngines.Remove(w); Schedule(); }

    public void RestoreDefaultWeb()
    {
        WebEngines.Clear();
        foreach (var w in WebSearchEngine.Defaults()) WebEngines.Add(new WebEngineViewModel(w, Schedule));
        Schedule();
    }

    private static bool ValidWeb(List<WebSearchEngine> list) =>
        list.All(w => w.Keyword.Length > 0 && !w.Keyword.Contains(' ') && w.UrlTemplate.Contains("{q}"))
        && list.Select(w => w.Keyword.ToLowerInvariant()).Distinct().Count() == list.Count;

    public string WebError => ValidWeb(WebEngines.Select(w => w.Engine).ToList()) ? "" : LocalizationManager.T("Settings.Web.Invalid");

    // ---------------- About
    public string Version => typeof(SettingsViewModel).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";
    public string DataDirectory => AppPathsInfo.Roaming;
    public string PerfSummary => PerfLog.Summary();
    public void RefreshPerf() => OnPropertyChanged(nameof(PerfSummary));

    public void RefreshLocalization()
    {
        OnPropertyChanged(nameof(Themes));
        OnPropertyChanged(nameof(Kinds));
        OnPropertyChanged(nameof(Targets));
        OnPropertyChanged(nameof(PrivilegeText));
        OnPropertyChanged(nameof(SortModes));
        // Replacing a ComboBox's ItemsSource clears its selection; re-push the selected values afterwards.
        System.Windows.Application.Current.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
        {
            OnPropertyChanged(nameof(Theme));
            OnPropertyChanged(nameof(SortBy));
            OnPropertyChanged(nameof(Language));
        });
    }

    public void Dispose()
    {
        _store.Changed -= OnStoreChanged;
        if (_saveTimer.IsEnabled) SaveNow();
    }
}

internal static class AppPathsInfo
{
    public static string Roaming => Core.Config.AppPaths.RoamingDirectory;
}


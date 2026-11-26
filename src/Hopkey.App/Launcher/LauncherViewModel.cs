using System.Windows.Threading;
using Hopkey.App.Infrastructure;
using Hopkey.App.Mvvm;
using Hopkey.Core.Config;
using Hopkey.Core.History;
using Hopkey.Core.Indexing;
using Hopkey.Core.Search;
using Hopkey.Core.Util;

namespace Hopkey.App.Launcher;

public sealed class LauncherViewModel : ObservableObject
{
    private readonly SearchEngine _engine;
    private readonly IconCache _icons;
    private readonly HistoryStore _history;
    private readonly SettingsStore _settings;
    private readonly Dispatcher _ui;
    private readonly DispatcherTimer _debounce;
    private CancellationTokenSource? _cts;
    private long _searchStart;
    private int _searchSeq;

    private string _query = "";
    private IReadOnlyList<ResultItemViewModel> _results = Array.Empty<ResultItemViewModel>();
    private int _selectedIndex = -1;
    private bool _actionMode;
    private IReadOnlyList<ActionItemViewModel> _actions = Array.Empty<ActionItemViewModel>();
    private int _selectedActionIndex;
    private string _indexStatus = "";
    private bool _indexError;
    private string? _message;
    private bool _messageIsError;
    private LauncherContext _context = LauncherContext.Normal;
    private bool _showEmptyHint;
    private bool _showNoResults;

    public LauncherViewModel(SearchEngine engine, IconCache icons, HistoryStore history, SettingsStore settings, Dispatcher ui)
    {
        _engine = engine;
        _icons = icons;
        _history = history;
        _settings = settings;
        _ui = ui;
        _debounce = new DispatcherTimer(DispatcherPriority.Input, ui) { Interval = TimeSpan.FromMilliseconds(30) };
        _debounce.Tick += (_, _) => { _debounce.Stop(); RunSearch(); };
    }

    public string Query
    {
        get => _query;
        set
        {
            if (!Set(ref _query, value ?? "")) return;
            ActionMode = false;
            _debounce.Stop();
            _debounce.Start();
        }
    }

    public IReadOnlyList<ResultItemViewModel> Results
    {
        get => _results;
        private set => Set(ref _results, value);
    }

    public int SelectedIndex
    {
        get => _selectedIndex;
        set => Set(ref _selectedIndex, value);
    }

    public ResultItemViewModel? Selected => _selectedIndex >= 0 && _selectedIndex < _results.Count ? _results[_selectedIndex] : null;

    public bool ActionMode
    {
        get => _actionMode;
        set => Set(ref _actionMode, value);
    }

    public IReadOnlyList<ActionItemViewModel> Actions
    {
        get => _actions;
        private set => Set(ref _actions, value);
    }

    public int SelectedActionIndex
    {
        get => _selectedActionIndex;
        set => Set(ref _selectedActionIndex, value);
    }

    public string IndexStatus
    {
        get => _indexStatus;
        private set => Set(ref _indexStatus, value);
    }

    public bool IndexError
    {
        get => _indexError;
        private set => Set(ref _indexError, value);
    }

    public string? Message
    {
        get => _message;
        private set { Set(ref _message, value); OnPropertyChanged(nameof(HasMessage)); }
    }

    public bool MessageIsError
    {
        get => _messageIsError;
        private set => Set(ref _messageIsError, value);
    }

    public bool HasMessage => !string.IsNullOrEmpty(_message);

    public LauncherContext Context
    {
        get => _context;
        set
        {
            if (Set(ref _context, value))
            {
                OnPropertyChanged(nameof(IsContextMode));
                OnPropertyChanged(nameof(ContextText));
            }
        }
    }

    public bool IsContextMode => _context.IsContext;
    public string ContextText => _context.IsContext ? LocalizationManager.F("Launcher.ContextTag", _context.Title) : "";

    public bool ShowEmptyHint
    {
        get => _showEmptyHint;
        private set => Set(ref _showEmptyHint, value);
    }

    public bool ShowNoResults
    {
        get => _showNoResults;
        private set => Set(ref _showNoResults, value);
    }

    public string KeyHints => LocalizationManager.T("Launcher.KeyHints");

    public void RefreshLocalization()
    {
        OnPropertyChanged(nameof(KeyHints));
        OnPropertyChanged(nameof(ContextText));
        if (_lastStatus != null) UpdateIndexStatus(_lastStatus);
    }

    private IndexStatus? _lastStatus;

    public void UpdateIndexStatus(IndexStatus s)
    {
        _lastStatus = s;
        IndexError = s.State == IndexState.Error;
        IndexStatus = s.State switch
        {
            IndexState.Loading => LocalizationManager.T("Launcher.IndexLoading"),
            IndexState.Building => LocalizationManager.F("Launcher.Indexing", s.Count),
            IndexState.Error => LocalizationManager.T("Launcher.IndexError"),
            IndexState.Paused => LocalizationManager.T("Launcher.IndexPaused"),
            IndexState.Ready => LocalizationManager.F("Launcher.Indexed", s.Count),
            _ => "",
        };
    }

    public async void ShowMessage(string text, bool error = false, int ms = 3000)
    {
        Message = text;
        MessageIsError = error;
        await Task.Delay(ms);
        if (Message == text) Message = null;
    }

    /// <summary>Search immediately (no debounce), e.g. when the window is shown.</summary>
    public void SearchNow()
    {
        _debounce.Stop();
        RunSearch();
    }

    private async void RunSearch()
    {
        _cts?.Cancel();
        var cts = new CancellationTokenSource();
        _cts = cts;
        int seq = ++_searchSeq;
        var text = _query;
        _searchStart = PerfLog.Start();
        IReadOnlyList<SearchResult> results;
        try
        {
            results = await _engine.SearchAsync(text, cts.Token);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            Logger.Error("Search failed", ex);
            return;
        }
        if (cts.IsCancellationRequested || seq != _searchSeq) return;
        PerfLog.Record("search.query", _engine.LastSearchTime.TotalMilliseconds);

        var list = new List<ResultItemViewModel>(results.Count + 1);
        for (int i = 0; i < results.Count; i++) list.Add(new ResultItemViewModel(results[i], i, _icons, _ui));

        bool empty = string.IsNullOrWhiteSpace(text);
        if (!empty && list.Count == 0)
        {
            var web = _settings.Current.WebSearch.FirstOrDefault(w => w.Enabled);
            if (web != null)
            {
                var q = text.Trim();
                list.Add(new ResultItemViewModel(new SearchResult
                {
                    Kind = ResultKind.Web,
                    Title = LocalizationManager.F("Launcher.SearchWeb", web.Name, q),
                    Subtitle = web.BuildUrl(q),
                    Path = web.BuildUrl(q),
                }, 0, _icons, _ui));
            }
        }
        var prevKey = Selected?.Result.Key;
        Results = list;
        int idx = prevKey == null ? -1 : list.FindIndex(r => r.Result.Key == prevKey);
        SelectedIndex = list.Count == 0 ? -1 : Math.Max(0, idx);
        ShowEmptyHint = empty && results.Count == 0;
        ShowNoResults = !empty && results.Count == 0;
        _ = _ui.BeginInvoke(DispatcherPriority.Loaded, () => PerfLog.Stop("search.render", _searchStart));
    }

    public void MoveSelection(int delta)
    {
        if (ActionMode)
        {
            if (_actions.Count == 0) return;
            SelectedActionIndex = Math.Clamp(_selectedActionIndex + delta, 0, _actions.Count - 1);
            return;
        }
        if (_results.Count == 0) return;
        SelectedIndex = Math.Clamp(_selectedIndex + delta, 0, _results.Count - 1);
    }

    public void SelectEdge(bool first)
    {
        if (ActionMode) { SelectedActionIndex = first ? 0 : Math.Max(0, _actions.Count - 1); return; }
        if (_results.Count > 0) SelectedIndex = first ? 0 : _results.Count - 1;
    }

    public bool EnterActionMode()
    {
        var sel = Selected;
        if (sel == null) return false;
        var r = sel.Result;
        bool fav = r.Path != null && _settings.Current.Favorites.Any(f => PathUtil.Equal(f.Path, r.Path));
        Actions = ResultActions.For(r, fav, r.Path != null && _history.HasUsage(r.Path));
        SelectedActionIndex = 0;
        ActionMode = Actions.Count > 0;
        return ActionMode;
    }

    public ActionItemViewModel? SelectedAction => ActionMode && _selectedActionIndex >= 0 && _selectedActionIndex < _actions.Count ? _actions[_selectedActionIndex] : null;

    public void Reset(bool clearQuery)
    {
        ActionMode = false;
        Message = null;
        if (clearQuery) { _query = ""; OnPropertyChanged(nameof(Query)); }
    }

    /// <summary>Release result VMs/icons while hidden (memory).</summary>
    public void ReleaseResults()
    {
        Results = Array.Empty<ResultItemViewModel>();
        SelectedIndex = -1;
    }
}

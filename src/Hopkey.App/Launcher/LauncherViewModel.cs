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
        var ss = _settings.Current.Search;
        var detail = new ResultDetailOptions(ss.ShowCreated, ss.ShowModified, ss.ShowSize);
        for (int i = 0; i < results.Count; i++) list.Add(new ResultItemViewModel(results[i], i, _icons, _ui, detail));

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
        UpdateFacets(text, results);
        var prevKey = Selected?.Result.Key;
        Results = list;
        int idx = prevKey == null ? -1 : list.FindIndex(r => r.Result.Key == prevKey);
        SelectedIndex = list.Count == 0 ? -1 : Math.Max(0, idx);
        ShowEmptyHint = empty && results.Count == 0;
        ShowNoResults = !empty && results.Count == 0;
        _ = _ui.BeginInvoke(DispatcherPriority.Loaded, () => PerfLog.Stop("search.render", _searchStart));
    }

    // ------------------------------------------------------------------ type facets

    private static readonly string[] s_filterPrefixes = { "folder:", "dir:", "file:", "app:", "ext:" };
    private const int MaxExtFacets = 6;
    private IReadOnlyList<FacetViewModel> _facets = Array.Empty<FacetViewModel>();
    /// <summary>Facets computed from the last unfiltered search, and that search's text (without filter tokens).</summary>
    private List<(string Label, int Count, string? Token)> _baseFacets = new();
    private string? _baseText;

    public IReadOnlyList<FacetViewModel> Facets
    {
        get => _facets;
        private set { Set(ref _facets, value); OnPropertyChanged(nameof(HasFacets)); }
    }

    public bool HasFacets => _facets.Count > 0;

    /// <summary>Query text with type/extension filter tokens removed.</summary>
    public static string StripFilters(string text, out string? token)
    {
        token = null;
        var kept = new List<string>();
        foreach (var t in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var lower = t.ToLowerInvariant();
            var p = s_filterPrefixes.FirstOrDefault(lower.StartsWith);
            // "folder:" / "ext:pdf" are pure filters; "folder:abc" also carries a keyword, keep those as typed.
            if (p != null && (p == "ext:" || lower.Length == p.Length))
            {
                token = p == "dir:" ? "folder:" : lower;
                continue;
            }
            kept.Add(t);
        }
        return string.Join(' ', kept);
    }

    private void UpdateFacets(string text, IReadOnlyList<SearchResult> results)
    {
        var stripped = StripFilters(text, out var activeToken).Trim();
        if (stripped.Length == 0) { _baseFacets.Clear(); _baseText = null; Facets = Array.Empty<FacetViewModel>(); return; }

        if (activeToken == null)
        {
            // Unfiltered search: (re)compute the facet set from what it found.
            int folders = 0, apps = 0;
            var exts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in results)
            {
                switch (r.Kind)
                {
                    case ResultKind.Folder: folders++; break;
                    case ResultKind.App: apps++; break;
                    case ResultKind.File:
                        var ext = System.IO.Path.GetExtension(r.Path ?? r.Title);
                        if (ext.Length > 1) exts[ext.ToLowerInvariant()] = exts.GetValueOrDefault(ext.ToLowerInvariant()) + 1;
                        break;
                }
            }
            var f = new List<(string Label, int Count, string? Token)>();
            if (folders > 0) f.Add((LocalizationManager.T("Launcher.Facet.Folder"), folders, "folder:"));
            if (apps > 0) f.Add((LocalizationManager.T("Launcher.Facet.App"), apps, "app:"));
            // Which extensions get a chip: the user's most-clicked ones first, then the most frequent in these results.
            foreach (var (ext, n) in exts
                         .OrderByDescending(kv => _history.GetFacetCount("ext:" + kv.Key[1..]))
                         .ThenByDescending(kv => kv.Value).ThenBy(kv => kv.Key).Take(MaxExtFacets))
                f.Add((ext[1..].ToUpperInvariant(), n, "ext:" + ext[1..]));
            // Chip order: more clicks → further left. Stable, so ties keep folders, apps, then extensions by count.
            _baseFacets = f.Select((x, i) => (x, i))
                .OrderByDescending(p => _history.GetFacetCount(p.x.Token!))
                .ThenBy(p => p.i)
                .Select(p => p.x).ToList();
            _baseText = stripped;
        }
        else if (!string.Equals(_baseText, stripped, StringComparison.OrdinalIgnoreCase))
        {
            // Filter typed by hand for a new search: nothing to switch between.
            _baseFacets.Clear();
            _baseText = null;
        }

        // Only worth showing when there are at least two kinds to choose from (or a filter to clear).
        if (_baseFacets.Count < 2 && activeToken == null) { Facets = Array.Empty<FacetViewModel>(); return; }
        if (_baseFacets.Count == 0) { Facets = Array.Empty<FacetViewModel>(); return; }
        var list = new List<FacetViewModel>(_baseFacets.Count + 1)
        {
            new() { Label = LocalizationManager.T("Launcher.Facet.All"), Token = null, IsActive = activeToken == null },
        };
        foreach (var (label, count, token) in _baseFacets)
            list.Add(new FacetViewModel { Label = label, Count = count, Token = token, IsActive = string.Equals(token, activeToken, StringComparison.OrdinalIgnoreCase) });
        Facets = list;
    }

    /// <summary>Switch the query to the given facet's filter (re-runs the search over the whole index).</summary>
    public void ApplyFacet(FacetViewModel f, bool recordUsage = false)
    {
        if (recordUsage && f.Token != null && !f.IsActive) _history.RecordFacet(f.Token);
        var stripped = StripFilters(_query, out _).Trim();
        Query = f.Token == null ? stripped : stripped + " " + f.Token;
    }

    /// <summary>Ctrl+Tab / Ctrl+Shift+Tab: move to the next/previous facet. Returns false if there are none.</summary>
    public bool CycleFacet(int delta)
    {
        if (_facets.Count == 0) return false;
        int cur = 0;
        for (int i = 0; i < _facets.Count; i++) if (_facets[i].IsActive) { cur = i; break; }
        ApplyFacet(_facets[((cur + delta) % _facets.Count + _facets.Count) % _facets.Count]);
        return true;
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

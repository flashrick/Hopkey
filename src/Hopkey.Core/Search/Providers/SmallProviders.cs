using Hopkey.Core.Config;
using Hopkey.Core.History;
using Hopkey.Core.Indexing;
using Hopkey.Core.Pinyin;
using Hopkey.Core.Util;

namespace Hopkey.Core.Search.Providers;

/// <summary>Start-menu / UWP applications. Acronym + fuzzy + pinyin allowed (small set).</summary>
public sealed class AppSearchProvider : ISearchProvider
{
    private readonly AppCatalog _catalog;
    public string Name => "apps";
    public AppSearchProvider(AppCatalog catalog) => _catalog = catalog;

    public bool CanHandle(SearchQuery q) => q.Keywords.Count > 0 && q.Type is TypeFilter.Any or TypeFilter.App && q.Extensions == null;

    public IReadOnlyList<SearchResult> Search(SearchQuery q, int maxResults, CancellationToken ct)
    {
        var list = new List<SearchResult>();
        foreach (var app in _catalog.Apps)
        {
            var hl = new List<(int, int)>();
            double s = NameMatcher.ScoreItem(app.Name, app.Lower, app.Initials, app.FullPinyin, app.Acronym, q.Keywords, hl);
            if (s < 0) continue;
            list.Add(new SearchResult
            {
                Kind = ResultKind.App,
                Title = app.Name,
                Subtitle = app.IsUwp ? "Microsoft Store App" : app.Path,
                Path = app.Path,
                Score = s + 200,
                Highlights = hl,
                IsUwp = app.IsUwp,
            });
        }
        return list;
    }
}

/// <summary>Empty query: favorites + recent items + recent folders (grouped). Non-empty: matches favorites (alias) and history.</summary>
public sealed class HistorySearchProvider : ISearchProvider
{
    private readonly HistoryStore _history;
    private readonly Func<AppSettings> _settings;
    public string Name => "history";

    public HistorySearchProvider(HistoryStore history, Func<AppSettings> settings)
    {
        _history = history;
        _settings = settings;
    }

    public bool CanHandle(SearchQuery q) => q.Web == null;

    public IReadOnlyList<SearchResult> Search(SearchQuery q, int maxResults, CancellationToken ct)
    {
        var settings = _settings();
        var list = new List<SearchResult>();
        if (q.Keywords.Count == 0)
        {
            if (q.Type != TypeFilter.Any || q.Extensions != null) return list;
            double baseScore = 100000;
            foreach (var f in settings.Favorites)
            {
                bool dir = Directory.Exists(f.Path);
                list.Add(new SearchResult
                {
                    Kind = dir ? ResultKind.Folder : ResultKind.File,
                    Title = string.IsNullOrEmpty(f.Alias) ? PathUtil.GetDisplayName(f.Path) : f.Alias!,
                    Subtitle = f.Path, Path = f.Path, Score = baseScore--, Group = ResultGroups.Favorites, IsFavorite = true,
                });
            }
            baseScore = 50000;
            foreach (var u in _history.GetRecentUsage(8))
            {
                if (u.Kind is UsageKind.Web or UsageKind.Calculator or UsageKind.Command) continue;
                list.Add(FromUsage(u, baseScore--, ResultGroups.Recent));
            }
            baseScore = 10000;
            foreach (var r in _history.GetRecentFolders(8))
            {
                list.Add(new SearchResult
                {
                    Kind = ResultKind.Folder, Title = PathUtil.GetDisplayName(r.Path), Subtitle = r.Path, Path = r.Path,
                    Score = baseScore--, Group = ResultGroups.RecentFolders,
                });
            }
            return list;
        }

        // Favorites by alias / name
        foreach (var f in settings.Favorites)
        {
            var title = string.IsNullOrEmpty(f.Alias) ? PathUtil.GetDisplayName(f.Path) : f.Alias!;
            var hl = new List<(int, int)>();
            double s = MatchName(title, q, hl);
            if (s < 0 && !string.IsNullOrEmpty(f.Alias))
            {
                var alt = PathUtil.GetDisplayName(f.Path);
                s = MatchName(alt, q, new List<(int, int)>());
                hl.Clear();
            }
            if (s < 0) continue;
            bool dir = Directory.Exists(f.Path);
            if (dir ? q.Type == TypeFilter.File : q.Type == TypeFilter.Folder) continue;
            list.Add(new SearchResult
            {
                Kind = dir ? ResultKind.Folder : ResultKind.File, Title = title, Subtitle = f.Path, Path = f.Path,
                Score = s + 400, Highlights = hl, IsFavorite = true,
            });
        }

        // Frequently used items (guarantees they aren't cut by phase-1 pre-scoring)
        foreach (var u in _history.GetTop(200))
        {
            if (u.Kind is UsageKind.Web or UsageKind.Calculator or UsageKind.Command) continue;
            if (u.Kind == UsageKind.App && q.Type is TypeFilter.File or TypeFilter.Folder) continue;
            if (u.Kind == UsageKind.Folder && q.Type == TypeFilter.File) continue;
            if (u.Kind == UsageKind.File && q.Type == TypeFilter.Folder) continue;
            if (q.Type == TypeFilter.App && u.Kind != UsageKind.App) continue;
            var name = u.Kind == UsageKind.App && u.Key.StartsWith("shell:", StringComparison.OrdinalIgnoreCase)
                ? u.Key[(u.Key.LastIndexOf('\\') + 1)..]
                : (u.Kind == UsageKind.App ? Path.GetFileNameWithoutExtension(u.Key) : PathUtil.GetDisplayName(u.Key));
            if (q.Extensions != null && !q.Extensions.Any(e => u.Key.EndsWith(e, StringComparison.OrdinalIgnoreCase))) continue;
            var hl = new List<(int, int)>();
            double s = MatchName(name, q, hl);
            if (s < 0) continue;
            var r = FromUsage(u, s, null, name, hl);
            list.Add(r);
        }
        return list;
    }

    private static double MatchName(string name, SearchQuery q, List<(int, int)> hl)
    {
        var lower = QueryParser.Lower(name);
        return NameMatcher.ScoreItem(name, lower, PinyinHelper.GetInitials(name), PinyinHelper.GetFullPinyin(name),
            AppCatalog.BuildAcronym(name), q.Keywords, hl, allowFuzzy: false);
    }

    private static SearchResult FromUsage(UsageRecord u, double score, string? group, string? title = null, List<(int, int)>? hl = null)
    {
        var kind = u.Kind switch
        {
            UsageKind.Folder => ResultKind.Folder,
            UsageKind.App => ResultKind.App,
            _ => ResultKind.File,
        };
        bool uwp = u.Key.StartsWith("shell:AppsFolder", StringComparison.OrdinalIgnoreCase);
        title ??= kind == ResultKind.App ? (uwp ? u.Key[(u.Key.LastIndexOf('\\') + 1)..] : Path.GetFileNameWithoutExtension(u.Key)) : PathUtil.GetDisplayName(u.Key);
        return new SearchResult
        {
            Kind = kind, Title = title, Subtitle = u.Key, Path = u.Key, Score = score, Group = group, IsUwp = uwp,
            Highlights = (IReadOnlyList<(int, int)>?)hl ?? Array.Empty<(int, int)>(),
        };
    }
}

public sealed class WebSearchProvider : ISearchProvider
{
    public string Name => "web";
    public bool CanHandle(SearchQuery q) => q.Web != null && !string.IsNullOrWhiteSpace(q.WebText);

    public IReadOnlyList<SearchResult> Search(SearchQuery q, int maxResults, CancellationToken ct)
    {
        var w = q.Web!;
        var url = w.BuildUrl(q.WebText!);
        return new[]
        {
            new SearchResult
            {
                Kind = ResultKind.Web, Title = $"{w.Name}: {q.WebText}", Subtitle = url, Path = url, Score = 1_000_000,
                Arguments = w.Keyword,
            },
        };
    }
}

public sealed class CalculatorProvider : ISearchProvider
{
    public string Name => "calc";
    public bool CanHandle(SearchQuery q) => q.Expression != null;

    public IReadOnlyList<SearchResult> Search(SearchQuery q, int maxResults, CancellationToken ct)
    {
        if (!Calculator.TryEvaluate(q.Expression!, out double v)) return Array.Empty<SearchResult>();
        var text = Calculator.Format(v);
        return new[]
        {
            new SearchResult
            {
                Kind = ResultKind.Calculator, Title = "= " + text, Subtitle = q.Expression, CopyText = text, Score = 2_000_000,
            },
        };
    }
}

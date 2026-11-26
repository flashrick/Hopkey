using System.Diagnostics;
using Hopkey.Core.Config;
using Hopkey.Core.Exclusion;
using Hopkey.Core.History;
using Hopkey.Core.Util;

namespace Hopkey.Core.Search;

/// <summary>Runs providers concurrently, merges, applies history boost and exclusion post-filter, de-duplicates.</summary>
public sealed class SearchEngine
{
    private readonly ISearchProvider[] _providers;
    private readonly HistoryStore _history;
    private readonly Func<ExclusionMatcher> _matcher;
    private readonly Func<AppSettings> _settings;

    public TimeSpan LastSearchTime { get; private set; }

    public SearchEngine(IEnumerable<ISearchProvider> providers, HistoryStore history, Func<ExclusionMatcher> matcher, Func<AppSettings> settings)
    {
        _providers = providers.ToArray();
        _history = history;
        _matcher = matcher;
        _settings = settings;
    }

    public SearchQuery Parse(string text) => QueryParser.Parse(text, _settings().WebSearch);

    public Task<IReadOnlyList<SearchResult>> SearchAsync(string text, CancellationToken ct) =>
        Task.Run(() => Search(text, ct), ct);

    public IReadOnlyList<SearchResult> Search(string text, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var settings = _settings();
        var q = QueryParser.Parse(text, settings.WebSearch);
        int max = Math.Clamp(settings.Search.MaxResults, 5, 500);

        var active = _providers.Where(p => p.CanHandle(q)).ToArray();
        var buckets = new IReadOnlyList<SearchResult>[active.Length];
        if (active.Length == 1) buckets[0] = SafeSearch(active[0], q, max, ct);
        else Parallel.For(0, active.Length, new ParallelOptions { CancellationToken = ct }, i => buckets[i] = SafeSearch(active[i], q, max, ct));
        ct.ThrowIfCancellationRequested();

        var matcher = _matcher();
        var byKey = new Dictionary<string, SearchResult>(StringComparer.OrdinalIgnoreCase);
        var others = new List<SearchResult>();
        bool boost = settings.Search.UseHistoryBoost && q.Keywords.Count > 0;
        foreach (var bucket in buckets)
        {
            if (bucket == null) continue;
            foreach (var r in bucket)
            {
                if (r.Path == null || r.Kind is ResultKind.Web or ResultKind.Calculator or ResultKind.Command) { others.Add(r); continue; }
                if (r.IsFileSystem && matcher.IsExcludedPath(r.Path, r.Kind == ResultKind.Folder)) continue;
                if (boost) r.Score += _history.GetBoost(r.Path, q.Normalized);
                if (byKey.TryGetValue(r.Path, out var existing))
                {
                    // Prefer the richer entry (higher score; keep favorite/group flags).
                    if (r.Score > existing.Score) byKey[r.Path] = r;
                }
                else byKey[r.Path] = r;
            }
        }
        var all = new List<SearchResult>(others.Count + byKey.Count);
        all.AddRange(others);
        all.AddRange(byKey.Values);
        all.Sort((a, b) => b.Score.CompareTo(a.Score));
        if (all.Count > max) all.RemoveRange(max, all.Count - max);
        LastSearchTime = sw.Elapsed;
        return all;
    }

    private static IReadOnlyList<SearchResult> SafeSearch(ISearchProvider p, SearchQuery q, int max, CancellationToken ct)
    {
        try { return p.Search(q, max, ct); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Logger.Error($"Search provider {p.Name} failed", ex);
            return Array.Empty<SearchResult>();
        }
    }
}

using Hopkey.Core.Config;
using Hopkey.Core.Pinyin;

namespace Hopkey.Core.Search;

/// <summary>
/// Optional "sort by date/size" for keyword searches. Keyword match stays the primary key: file/folder results are
/// grouped into match tiers (exact name &gt; name prefix &gt; word start &gt; anything else) and only reordered by the
/// chosen metadata inside a tier. Other results (calculator, web, apps) keep their score order ahead of files.
/// Metadata isn't in the index, so it's read from disk here — only for the merged candidate list.
/// </summary>
public static class ResultSorter
{
    public static void Apply(List<SearchResult> results, SearchQuery q, ResultSortMode mode, CancellationToken ct)
    {
        if (mode == ResultSortMode.Relevance || q.Keywords.Count == 0 || results.Count < 2) return;

        var keys = new Dictionary<SearchResult, (int Tier, long Meta)>(results.Count, ReferenceEqualityComparer.Instance);
        foreach (var r in results)
        {
            ct.ThrowIfCancellationRequested();
            if (r.Kind is ResultKind.File or ResultKind.Folder && r.Path != null)
                keys[r] = (MatchTier(r.Title, q.Keywords), ReadMeta(r.Path, r.Kind == ResultKind.Folder, mode));
        }

        // Stable: equal keys keep their relevance order.
        var ordered = results
            .Select((r, i) => (r, i))
            .OrderBy(x => keys.ContainsKey(x.r) ? 1 : 0)                       // non-file results first (score order)
            .ThenByDescending(x => keys.TryGetValue(x.r, out var k) ? k.Tier : 0)
            .ThenByDescending(x => keys.TryGetValue(x.r, out var k) ? k.Meta : 0)
            .ThenBy(x => x.i)
            .Select(x => x.r)
            .ToList();
        results.Clear();
        results.AddRange(ordered);
    }

    /// <summary>Weakest match class over all keywords: 3 exact, 2 prefix, 1 word start, 0 other (substring/path/fuzzy).</summary>
    public static int MatchTier(string title, IReadOnlyList<string> keywords)
    {
        var lower = QueryParser.Lower(title);
        string? initials = null;
        int tier = 3;
        foreach (var kw in keywords)
        {
            double s = NameMatcher.ScoreSubstring(lower, title, kw, out _);
            if (s < 0)
            {
                initials ??= PinyinHelper.GetInitials(title);
                s = NameMatcher.ScoreSubstring(initials, initials, kw, out _);
            }
            int t = s >= NameMatcher.Exact ? 3 : s >= NameMatcher.Prefix ? 2 : s > NameMatcher.Substring ? 1 : 0;
            if (t < tier) tier = t;
            if (tier == 0) break;
        }
        return tier;
    }

    private static long ReadMeta(string path, bool folder, ResultSortMode mode)
    {
        try
        {
            FileSystemInfo fi = folder ? new DirectoryInfo(path) : new FileInfo(path);
            if (!fi.Exists) return long.MinValue;
            return mode switch
            {
                ResultSortMode.ModifiedNewest => fi.LastWriteTimeUtc.Ticks,
                ResultSortMode.CreatedNewest => fi.CreationTimeUtc.Ticks,
                ResultSortMode.SizeLargest => fi is FileInfo f ? f.Length : -1, // folders after files
                _ => 0,
            };
        }
        catch { return long.MinValue; }
    }
}

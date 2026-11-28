using Hopkey.Core.Config;
using Hopkey.Core.Indexing;

namespace Hopkey.Core.Search.Providers;

/// <summary>
/// File index search: phase 1 (ArenaScanner) on the anchor keyword (last keyword), phase 2 exact scoring of
/// ≤ ~4k survivors with the remaining keywords matched against the name or ancestor folder names.
/// Supports incremental narrowing when the user keeps typing.
/// </summary>
public sealed class IndexSearchProvider : ISearchProvider
{
    private readonly FileIndex _index;
    private readonly Func<SearchSettings> _settings;
    private readonly object _cacheGate = new();
    private NarrowCache? _cache;

    private sealed record NarrowCache(string Anchor, TypeFilter Type, string? Ext, VolumeIndex[] Volumes, List<ScanCandidate> Candidates);

    public string Name => "index";

    public IndexSearchProvider(FileIndex index, Func<SearchSettings> settings)
    {
        _index = index;
        _settings = settings;
        // The cache is only valid for the exact volume array it was built on; drop it (and with it the replaced
        // volumes) as soon as the array is swapped.
        _index.VolumesReplaced += () => { lock (_cacheGate) _cache = null; };
    }

    public bool CanHandle(SearchQuery q) => q.Keywords.Count > 0 && q.Type != TypeFilter.App;

    public IReadOnlyList<SearchResult> Search(SearchQuery q, int maxResults, CancellationToken ct)
    {
        var volumes = _index.VolumesArray;
        if (volumes.Length == 0) return Array.Empty<SearchResult>();
        var s = _settings();
        var filter = new ScanFilter { Type = q.Type, Extensions = q.Extensions, Pinyin = s.Pinyin };

        // Anchor = last keyword (must hit the name); others may hit name or path.
        var results = RunWithAnchor(q, q.Keywords.Count - 1, volumes, filter, maxResults, ct);
        if (results.Count < maxResults && q.Keywords.Count > 1)
        {
            var more = RunWithAnchor(q, 0, volumes, filter, maxResults, ct);
            var seen = new HashSet<string>(results.Select(r => r.Path!), StringComparer.OrdinalIgnoreCase);
            foreach (var r in more) if (seen.Add(r.Path!)) results.Add(r);
        }
        return results;
    }

    private List<SearchResult> RunWithAnchor(SearchQuery q, int anchorIdx, VolumeIndex[] volumes, ScanFilter filter, int maxResults, CancellationToken ct)
    {
        string anchor = q.Keywords[anchorIdx];
        var extKey = filter.Extensions == null ? null : string.Join(",", filter.Extensions);
        List<ScanCandidate>? candidates = null;

        // Incremental narrowing: previous anchor is a prefix of this one and the previous scan was not truncated.
        if (anchorIdx == q.Keywords.Count - 1)
        {
            NarrowCache? c;
            lock (_cacheGate) c = _cache;
            if (c != null && c.Type == filter.Type && c.Ext == extKey && ReferenceEquals(c.Volumes, volumes) &&
                anchor.Length > c.Anchor.Length && anchor.StartsWith(c.Anchor, StringComparison.Ordinal))
            {
                candidates = Narrow(c, anchor, volumes, filter);
            }
        }
        if (candidates == null)
        {
            // Single keyword: the phase-1 pre-score is almost the final score, so a small K suffices.
            // Multiple keywords: other keywords filter survivors, so keep more.
            int k = q.Keywords.Count > 1 ? 1024 : Math.Max(512, maxResults * 8);
            candidates = ArenaScanner.Scan(volumes, anchor, filter, k, ct, out bool truncated);
            if (anchorIdx == q.Keywords.Count - 1)
                lock (_cacheGate) _cache = truncated ? null : new NarrowCache(anchor, filter.Type, extKey, volumes, candidates);
        }
        ct.ThrowIfCancellationRequested();
        return Phase2(q, anchorIdx, volumes, candidates, maxResults, ct);
    }

    private static List<ScanCandidate> Narrow(NarrowCache c, string anchor, VolumeIndex[] volumes, ScanFilter filter)
    {
        var list = new List<ScanCandidate>(c.Candidates.Count);
        foreach (var cand in c.Candidates)
        {
            var v = volumes[cand.Volume];
            v.Lock.EnterReadLock();
            try
            {
                if (cand.Entry >= v.Count || v.IsDeleted(cand.Entry)) continue;
                var lower = v.GetLowerName(cand.Entry);
                int pos = lower.IndexOf(anchor.AsSpan());
                if (pos >= 0)
                {
                    list.Add(cand with { PreScore = NameMatcher.ScoreAt(v.GetName(cand.Entry), lower.Length, anchor.Length, pos) + Bonus(v, cand.Entry, filter), ViaPinyin = false });
                    continue;
                }
                if (!filter.Pinyin) continue;
                var nm = v.GetName(cand.Entry);
                if (PinyinPos(nm, anchor) is int ppos && ppos >= 0)
                    list.Add(cand with { PreScore = NameMatcher.ScoreAt(nm, lower.Length, anchor.Length, ppos) * NameMatcher.PinyinFactor + Bonus(v, cand.Entry, filter), ViaPinyin = true });
                else if (FullPinyinFind(nm, anchor, out int fpos, out int flen))
                    list.Add(cand with { PreScore = NameMatcher.ScoreAt(nm, lower.Length, flen, fpos) * NameMatcher.PinyinFactor + Bonus(v, cand.Entry, filter), ViaPinyin = true });
            }
            finally { v.Lock.ExitReadLock(); }
        }
        return list;
    }

    private static List<SearchResult> Phase2(SearchQuery q, int anchorIdx, VolumeIndex[] volumes, List<ScanCandidate> candidates, int maxResults, CancellationToken ct)
    {
        // Lock each volume once; candidates are grouped by sorting (no LINQ GroupBy), no per-candidate allocations.
        candidates.Sort((a, b) => a.Volume != b.Volume ? a.Volume.CompareTo(b.Volume) : a.Entry.CompareTo(b.Entry));
        var scored = new List<(double Score, int Vol, int Entry)>(candidates.Count);
        var pathMemo = new Dictionary<long, bool>?[q.Keywords.Count];
        int idx = 0;
        while (idx < candidates.Count)
        {
            int vol = candidates[idx].Volume;
            var v = volumes[vol];
            v.Lock.EnterReadLock();
            try
            {
                int lastEntry = -1;
                for (; idx < candidates.Count && candidates[idx].Volume == vol; idx++)
                {
                    var c = candidates[idx];
                    int e = c.Entry;
                    if (e == lastEntry || e <= 0 || e >= v.Count) continue; // de-dup (pinyin + main hits)
                    lastEntry = e;
                    var name = v.GetName(e);
                    var lower = v.GetLowerName(e);
                    double score = 0;
                    bool ok = true;
                    for (int k = 0; k < q.Keywords.Count && ok; k++)
                    {
                        var kw = q.Keywords[k];
                        if (k == anchorIdx && !c.ViaPinyin && q.Keywords.Count == 1) { score += c.PreScore; continue; }
                        double s = NameMatcher.ScoreSubstring(lower, name, kw, out _);
                        if (s >= 0) { score += s; continue; }
                        if (k == anchorIdx && c.ViaPinyin) { score += c.PreScore; continue; }
                        if (k != anchorIdx && AncestorContains(v, vol, e, kw, pathMemo[k] ??= new())) { score += NameMatcher.PathHit; continue; }
                        if (k != anchorIdx && PinyinContains(name, kw)) { score += NameMatcher.Substring * NameMatcher.PinyinFactor; continue; }
                        ok = false;
                    }
                    if (!ok) continue;
                    // One parent walk: depth bonus + skip entries whose ancestor was deleted.
                    int depth = 0, j = v.Parent[e];
                    bool deadAncestor = false;
                    while (j > 0 && depth < 256)
                    {
                        if ((v.Flags[j] & IndexEntryFlags.Deleted) != 0) { deadAncestor = true; break; }
                        j = v.Parent[j];
                        depth++;
                    }
                    if (deadAncestor) continue;
                    if (q.Keywords.Count > 1)
                    {
                        score += 64 - Math.Min(name.Length, 64);
                        if (!v.IsDirectory(e)) score += NameMatcher.LaunchableBonus(lower);
                    }
                    score += 16 - Math.Min(depth, 16);
                    if (q.Keywords.Count > 1 && q.Type == TypeFilter.Any && v.IsDirectory(e)) score += 20;
                    scored.Add((score, vol, e));
                }
            }
            finally { v.Lock.ExitReadLock(); }
            ct.ThrowIfCancellationRequested();
        }

        scored.Sort((a, b) => b.Score.CompareTo(a.Score));
        int take = Math.Min(scored.Count, maxResults * 2);
        var results = new List<SearchResult>(take);
        for (int i = 0; i < take; i++)
        {
            var (score, vol, e) = scored[i];
            var v = volumes[vol];
            string path, title;
            bool dir;
            v.Lock.EnterReadLock();
            try
            {
                if (e >= v.Count || v.IsDeleted(e)) continue;
                path = v.GetFullPath(e);
                title = v.GetName(e).ToString();
                dir = v.IsDirectory(e);
            }
            finally { v.Lock.ExitReadLock(); }
            results.Add(new SearchResult
            {
                Kind = dir ? ResultKind.Folder : ResultKind.File,
                Title = title,
                Subtitle = path,
                Path = path,
                Score = score,
                Highlights = Highlights(title, q.Keywords),
            });
        }
        return results;
    }

    /// <summary>Same length/folder bonus as ArenaScanner.PreScore.</summary>
    private static double Bonus(VolumeIndex v, int e, ScanFilter filter)
    {
        double b = 64 - Math.Min((int)v.NameLength[e], 64);
        if (v.IsDirectory(e)) { if (filter.Type == TypeFilter.Any) b += 20; }
        else b += NameMatcher.LaunchableBonus(v.GetLowerName(e));
        return b;
    }

    private static bool FullPinyinFind(ReadOnlySpan<char> name, string kw, out int pos, out int len)
    {
        pos = len = 0;
        return Pinyin.PinyinMatcher.IsFullPinyinQuery(kw) && Pinyin.PinyinMatcher.Find(name, kw, out pos, out len);
    }

    /// <summary>Highlight ranges computed only for the final results.</summary>
    private static IReadOnlyList<(int, int)> Highlights(string title, IReadOnlyList<string> keywords)
    {
        var lower = QueryParser.Lower(title);
        var hl = new List<(int, int)>(keywords.Count);
        foreach (var kw in keywords)
        {
            if (NameMatcher.ScoreSubstring(lower, title, kw, out int pos) >= 0) hl.Add((pos, kw.Length));
            else if (Pinyin.PinyinHelper.ContainsCjk(title) && PinyinPos(title, kw) is int p && p >= 0) hl.Add((p, kw.Length));
            else if (FullPinyinFind(title, kw, out int fp, out int fl)) hl.Add((fp, fl));
        }
        return hl;
    }

    /// <summary>Position of <paramref name="kw"/> within the pinyin-initials string of <paramref name="name"/> (same length as name), or -1.</summary>
    private static int PinyinPos(ReadOnlySpan<char> name, string kw)
    {
        if (!Pinyin.PinyinHelper.ContainsCjk(name) || !Pinyin.PinyinHelper.IsPinyinQuery(kw)) return -1;
        Span<char> buf = name.Length <= 256 ? stackalloc char[name.Length] : new char[name.Length];
        Pinyin.PinyinHelper.WriteInitials(name, buf);
        return ((ReadOnlySpan<char>)buf).IndexOf(kw.AsSpan());
    }

    /// <summary>
    /// Does any ancestor folder name contain <paramref name="kw"/>? Memo maps (folder, kw) → "folder or one of its
    /// ancestors contains kw", so each folder is examined at most once per keyword per query.
    /// </summary>
    private static bool AncestorContains(VolumeIndex v, int vol, int e, string kw, Dictionary<long, bool> memo)
    {
        int j = v.Parent[e], guard = 0;
        var chain = new List<long>(8);
        bool found = false;
        while (j >= 0 && guard++ < 256)
        {
            long key = ((long)vol << 32) | (uint)j;
            if (memo.TryGetValue(key, out bool m)) { found = m; break; }
            chain.Add(key);
            var nm = j == 0 ? v.RootPathLower.AsSpan() : v.GetLowerName(j);
            if (nm.IndexOf(kw.AsSpan()) >= 0) { found = true; break; }
            j = j == 0 ? -1 : v.Parent[j];
        }
        foreach (var k in chain) memo[k] = found;
        return found;
    }

    private static bool PinyinContains(ReadOnlySpan<char> name, string kw)
    {
        if (!Pinyin.PinyinHelper.ContainsCjk(name) || !Pinyin.PinyinHelper.IsPinyinQuery(kw)) return false;
        Span<char> buf = name.Length <= 256 ? stackalloc char[name.Length] : new char[name.Length];
        Pinyin.PinyinHelper.WriteInitials(name, buf);
        return ((ReadOnlySpan<char>)buf).IndexOf(kw.AsSpan()) >= 0 || FullPinyinFind(name, kw, out _, out _);
    }
}

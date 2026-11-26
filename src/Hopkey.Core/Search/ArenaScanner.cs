using System.Runtime.CompilerServices;
using Hopkey.Core.Indexing;

namespace Hopkey.Core.Search;

public readonly record struct ScanCandidate(int Volume, int Entry, double PreScore, bool ViaPinyin);

public sealed class ScanFilter
{
    public TypeFilter Type { get; init; }
    public string[]? Extensions { get; init; }
    public bool Pinyin { get; init; }
}

/// <summary>
/// Phase 1: zero-allocation, vectorized IndexOf over the pre-lowercased name arena, partitioned across cores.
/// Each hit maps back to its entry by binary search over NameOffset; the scan then skips to the next name,
/// so each entry is visited at most once. Per-worker bounded heaps keep the best K by a cheap pre-score.
/// </summary>
public static class ArenaScanner
{
    public const int DefaultK = 4096;

    public static List<ScanCandidate> Scan(IReadOnlyList<VolumeIndex> volumes, string anchor, ScanFilter filter, int k, CancellationToken ct, out bool truncated)
    {
        truncated = false;
        var result = new List<ScanCandidate>(Math.Min(k, 1024));
        if (anchor.Length == 0 || volumes.Count == 0) return result;

        int workers = Environment.ProcessorCount;
        int partsPerVolume = Math.Max(1, workers * 2);
        // Main-arena partitions and pinyin-arena partitions run in the same parallel loop.
        var parts = new List<(int Vol, int From, int To, bool Pinyin)>(volumes.Count * partsPerVolume);
        for (int vi = 0; vi < volumes.Count; vi++)
        {
            int n = volumes[vi].Count;
            if (n <= 1) continue;
            int per = Math.Max(4096, (n + partsPerVolume - 1) / partsPerVolume);
            for (int a = 1; a < n; a += per) parts.Add((vi, a, Math.Min(n, a + per), false));
        }
        if (filter.Pinyin && Pinyin.PinyinHelper.IsPinyinQuery(anchor))
        {
            for (int vi = 0; vi < volumes.Count; vi++)
            {
                int slots = volumes[vi].Pinyin?.Count ?? 0;
                if (slots == 0) continue;
                int per = Math.Max(8192, (slots + workers - 1) / workers);
                for (int a = 0; a < slots; a += per) parts.Add((vi, a, Math.Min(slots, a + per), true));
            }
        }

        int localK = Math.Max(64, (k + workers - 1) / workers);
        var heaps = new System.Collections.Concurrent.ConcurrentBag<TopN>();

        Parallel.For(0, parts.Count, new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = workers },
            () => new TopN(localK),
            (pi, state, heap) =>
            {
                var (vol, from, to, py) = parts[pi];
                if (py) ScanPinyin(volumes[vol], vol, from, to, anchor, filter, heap, ct);
                else ScanPartition(volumes[vol], vol, from, to, anchor, filter, heap, ct);
                return heap;
            },
            heaps.Add);

        foreach (var h in heaps)
        {
            if (h.Pushed > h.Count) truncated = true;
            for (int i = 0; i < h.Count; i++)
            {
                var (score, a, b) = h[i];
                result.Add(new ScanCandidate(a & 0x3FFFFFFF, b, score, (a & 0x40000000) != 0));
            }
        }
        return result;
    }

    private static void ScanPartition(VolumeIndex v, int vol, int from, int to, string anchor, ScanFilter filter, TopN heap, CancellationToken ct)
    {
        v.Lock.EnterReadLock();
        try
        {
            int count = v.Count;
            if (from >= count) return;
            to = Math.Min(to, count);
            var lower = v.Lower.Data;
            var offs = v.NameOffset;
            int start = offs[from];
            int end = offs[to];
            var span = new ReadOnlySpan<char>(lower, start, end - start);
            var needle = anchor.AsSpan();
            int pos = 0, iter = 0, cur = from;
            while (pos < span.Length)
            {
                int hit = span[pos..].IndexOf(needle);
                if (hit < 0) break;
                int abs = start + pos + hit;
                int e = cur = Gallop(offs, cur, to - 1, abs);
                pos = offs[e + 1] - start;
                if ((++iter & 1023) == 0 && ct.IsCancellationRequested) return;
                if (!Accept(v, e, filter)) continue;
                int posInName = abs - offs[e];
                double pre = PreScore(v, e, posInName, anchor.Length, filter);
                heap.Push(pre, vol, e);
            }
        }
        finally { v.Lock.ExitReadLock(); }
    }

    private static void ScanPinyin(VolumeIndex v, int vol, int slotFrom, int slotTo, string anchor, ScanFilter filter, TopN heap, CancellationToken ct)
    {
        v.Lock.EnterReadLock();
        try
        {
            var py = v.Pinyin;
            if (py == null || py.Count == 0) return;
            slotTo = Math.Min(slotTo, py.Count);
            if (slotFrom >= slotTo) return;
            int start = py.Offsets[slotFrom];
            int end = slotTo < py.Count ? py.Offsets[slotTo] : py.Chars.Length;
            var span = new ReadOnlySpan<char>(py.Chars.Data, start, end - start);
            var offs = py.Offsets;
            var needle = anchor.AsSpan();
            int pos = 0, iter = 0, slot = slotFrom;
            while (pos < span.Length)
            {
                int hit = span[pos..].IndexOf(needle);
                if (hit < 0) break;
                int abs = start + pos + hit;
                slot = Gallop(offs, slot, slotTo - 1, abs);
                int e = py.EntryIds[slot];
                pos = (slot + 1 < py.Count ? offs[slot + 1] : py.Chars.Length) - start;
                if ((++iter & 1023) == 0 && ct.IsCancellationRequested) return;
                if (e <= 0 || e >= v.Count) continue;
                int posInName = abs - offs[slot];
                var name = v.GetName(e);
                if (posInName + anchor.Length > name.Length) continue; // stale slot (renamed)
                // Only useful if the matched range covers a CJK char; pure-ASCII matches come from the main scan.
                bool cjk = false;
                for (int i = posInName; i < posInName + anchor.Length; i++)
                    if (Pinyin.PinyinHelper.IsCjk(name[i])) { cjk = true; break; }
                if (!cjk) continue;
                if (!Accept(v, e, filter)) continue;
                double pre = PreScore(v, e, posInName, anchor.Length, filter) * NameMatcher.PinyinFactor;
                heap.Push(pre, vol | 0x40000000, e);
            }
        }
        finally { v.Lock.ExitReadLock(); }
    }

    /// <summary>
    /// Entry containing <paramref name="abs"/>, searching forward from <paramref name="cur"/> (hits arrive in increasing
    /// order). Exponential search makes dense hits (short anchors) amortized O(1) and sparse hits O(log gap).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Gallop(int[] offs, int cur, int hi, int abs)
    {
        if (cur >= hi || offs[cur + 1] > abs) return cur;
        int step = 1, lo = cur + 1;
        while (lo + step <= hi && offs[lo + step] <= abs) { lo += step; step <<= 1; }
        return EntryAt(offs, lo, Math.Min(hi, lo + step), abs);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int EntryAt(int[] offs, int lo, int hi, int abs)
    {
        while (lo < hi)
        {
            int mid = (lo + hi + 1) >> 1;
            if (offs[mid] <= abs) lo = mid; else hi = mid - 1;
        }
        return lo;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool Accept(VolumeIndex v, int e, ScanFilter filter)
    {
        var f = v.Flags[e];
        if ((f & IndexEntryFlags.Deleted) != 0) return false;
        bool dir = (f & IndexEntryFlags.Directory) != 0;
        if (filter.Type == TypeFilter.Folder && !dir) return false;
        if (filter.Type == TypeFilter.File && dir) return false;
        if (filter.Extensions != null)
        {
            if (dir) return false;
            var name = v.GetLowerName(e);
            bool ok = false;
            foreach (var ext in filter.Extensions)
                if (name.EndsWith(ext)) { ok = true; break; }
            if (!ok) return false;
        }
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double PreScore(VolumeIndex v, int e, int posInName, int kwLen, ScanFilter filter)
    {
        int len = v.NameLength[e];
        double s = NameMatcher.ScoreAt(v.GetName(e), len, kwLen, posInName);
        s += 64 - Math.Min(len, 64);
        if (filter.Type == TypeFilter.Any && (v.Flags[e] & IndexEntryFlags.Directory) != 0) s += 20;
        return s;
    }
}

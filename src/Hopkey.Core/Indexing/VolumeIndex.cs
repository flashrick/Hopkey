using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Hopkey.Core.Exclusion;
using Hopkey.Core.Pinyin;
using Hopkey.Core.Util;

namespace Hopkey.Core.Indexing;

/// <summary>
/// Compact struct-of-arrays index for one root. Entry 0 is the root.
/// Invariants: entries are append-only; NameOffset strictly increases with index (Names and Lower share layout);
/// for USN volumes Frn[1..BaseCount) is sorted ascending (later entries are found via Overflow).
/// Readers take Lock.EnterReadLock; the single writer takes the write lock.
/// </summary>
public sealed class VolumeIndex
{
    public string RootPath { get; }
    internal string RootPathLower => _rootLower ??= RootPath.ToLowerInvariant();
    private string? _rootLower;
    public IndexSourceKind Kind { get; }
    public uint VolumeSerial { get; set; }
    public ulong JournalId { get; set; }
    public long NextUsn { get; set; }
    public DateTime LastUpdatedUtc { get; set; } = DateTime.UtcNow;
    public TimeSpan? LastBuildTime { get; set; }

    internal int[] Parent;
    internal int[] NameOffset;
    internal byte[] NameLength;
    internal IndexEntryFlags[] Flags;
    internal ulong[]? Frn;
    internal int BaseCount;
    internal Dictionary<ulong, int>? Overflow;
    /// <summary>USN: base FRN → highest <see cref="LinkKey"/> ordinal of its extra hard-link entries.</summary>
    internal Dictionary<ulong, byte>? LinkedBases;
    internal NameArena Names;
    internal NameArena Lower;
    internal DirPathMap? DirPaths;
    internal PinyinArena? Pinyin;
    internal int PinyinVersion;

    internal int CountInternal;
    internal int DeletedCount;
    internal int GarbageChars;
    internal volatile bool Dirty;
    /// <summary>Set when changes moved content from excluded to included areas (needs rebuild of that root).</summary>
    internal volatile bool NeedsRebuild;

    private readonly ConcurrentDictionary<int, string> _dirPathCache = new();

    public ReaderWriterLockSlim Lock { get; } = new(LockRecursionPolicy.NoRecursion);

    public int Count => CountInternal;

    /// <summary>Diagnostics: approximate bytes held by each structure.</summary>
    public string DescribeMemory()
    {
        long arrays = Parent.Length * 4L + NameOffset.Length * 4L + NameLength.Length + Flags.Length + (Frn?.Length ?? 0) * 8L;
        long names = Names.Data.Length * 2L, lower = Lower.Data.Length * 2L;
        long dirPaths = 0;
        if (DirPaths != null) dirPaths = DirPaths.Count * 24L;
        long pinyin = Pinyin == null ? 0 : Pinyin.Chars.Data.Length * 2L + Pinyin.Offsets.Length * 8L;
        long overflow = (Overflow?.Count ?? 0) * 24L;
        long cache = _dirPathCache.Count * 120L;
        double mb(long b) => b / 1048576.0;
        return $"{RootPath}: {LiveCount:N0} live / {CountInternal:N0} slots (cap {Parent.Length:N0}); arrays {mb(arrays):F1} MB, names {mb(names):F1} MB (used {Names.Length:N0} chars), lower {mb(lower):F1} MB, " +
               $"dirPaths {DirPaths?.Count:N0} ≈ {mb(dirPaths):F1} MB, pinyin {mb(pinyin):F1} MB, overflow {mb(overflow):F1} MB, pathCache {mb(cache):F1} MB";
    }
    public int LiveCount => CountInternal - DeletedCount - 1;
    public int ArenaLength => Names.Length;

    internal VolumeIndex(string rootPath, IndexSourceKind kind, int capacity, int arenaCapacity)
    {
        RootPath = rootPath;
        Kind = kind;
        capacity = Math.Max(capacity, 16);
        Parent = new int[capacity];
        NameOffset = new int[capacity + 1];
        NameLength = new byte[capacity];
        Flags = new IndexEntryFlags[capacity];
        if (kind == IndexSourceKind.Usn) { Frn = new ulong[capacity]; Overflow = new Dictionary<ulong, int>(); }
        else DirPaths = new DirPathMap();
        Names = new NameArena(arenaCapacity);
        Lower = new NameArena(arenaCapacity);
    }

    /// <summary>Create an empty index containing only the root entry.</summary>
    internal static VolumeIndex CreateEmpty(string rootPath, IndexSourceKind kind, ulong rootKey, int capacity = 1024, int arenaCapacity = 16 * 1024)
    {
        var v = new VolumeIndex(rootPath, kind, capacity, arenaCapacity);
        v.AppendRaw(-1, PathUtil.TrimEndSeparator(rootPath).TrimEnd('\\'), IndexEntryFlags.Directory, rootKey);
        v.BaseCount = 1;
        if (v.DirPaths != null) v.DirPaths[PathUtil.TrimEndSeparator(rootPath)] = 0;
        return v;
    }

    // ------------------------------------------------------------------ reading

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlySpan<char> GetName(int i) => new(Names.Data, NameOffset[i], NameLength[i]);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ReadOnlySpan<char> GetLowerName(int i) => new(Lower.Data, NameOffset[i], NameLength[i]);

    public bool IsDirectory(int i) => (Flags[i] & IndexEntryFlags.Directory) != 0;
    public bool IsDeleted(int i) => (Flags[i] & IndexEntryFlags.Deleted) != 0;

    public string GetFullPath(int i)
    {
        if (i == 0) return RootPath;
        if (IsDirectory(i) && _dirPathCache.TryGetValue(i, out var cached)) return cached;
        Span<int> chain = stackalloc int[128];
        int depth = 0, len = 0, j = i;
        while (j > 0 && depth < chain.Length)
        {
            chain[depth++] = j;
            len += NameLength[j] + 1;
            j = Parent[j];
        }
        var root = RootPath;
        bool rootHasSep = root.EndsWith('\\');
        int total = root.Length + len - (rootHasSep ? 1 : 0);
        Span<char> buf = total <= 1024 ? stackalloc char[total] : new char[total];
        root.AsSpan().CopyTo(buf);
        int pos = root.Length;
        for (int k = depth - 1; k >= 0; k--)
        {
            if (!(rootHasSep && k == depth - 1)) buf[pos++] = '\\';
            var name = GetName(chain[k]);
            name.CopyTo(buf[pos..]);
            pos += name.Length;
        }
        var result = new string(buf[..pos]);
        if (IsDirectory(i))
        {
            if (_dirPathCache.Count > 8192) _dirPathCache.Clear();
            _dirPathCache[i] = result;
        }
        return result;
    }

    public int GetDepth(int i)
    {
        int d = 0;
        while (i > 0 && d < 256) { i = Parent[i]; d++; }
        return d;
    }

    /// <summary>Entry containing the given arena offset (binary search over NameOffset).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int EntryAtOffset(int arenaOffset)
    {
        int lo = 0, hi = CountInternal - 1;
        var offs = NameOffset;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) >> 1;
            if (offs[mid] <= arenaOffset) lo = mid; else hi = mid - 1;
        }
        return lo;
    }

    public bool TryFindByKey(ulong key, out int index)
    {
        index = -1;
        if (Frn == null) return false;
        if (Overflow != null && Overflow.TryGetValue(key, out index)) return true;
        if (CountInternal > 0 && Frn[0] == key) { index = 0; return true; }
        int pos = Array.BinarySearch(Frn, 1, Math.Max(0, BaseCount - 1), key);
        if (pos > 0 && (Flags[pos] & IndexEntryFlags.Deleted) == 0) { index = pos; return true; }
        index = -1;
        return false;
    }

    public bool TryFindDirectory(string fullPath, out int index)
    {
        index = -1;
        return DirPaths != null && DirPaths.TryGetValue(fullPath, out index) && index < CountInternal && !IsDeleted(index)
               && IsDirectory(index) && PathUtil.Equal(GetFullPath(index), fullPath);
    }

    /// <summary>Find the live entry for a full path (file or folder). O(n) for files; caller holds a lock.</summary>
    public int FindPath(string fullPath)
    {
        fullPath = PathUtil.TrimEndSeparator(fullPath);
        if (TryFindDirectory(fullPath, out int d)) return d;
        var parent = PathUtil.GetParent(fullPath);
        if (parent == null) return -1;
        int p;
        if (PathUtil.Equal(PathUtil.TrimEndSeparator(parent), PathUtil.TrimEndSeparator(RootPath))) p = 0;
        else if (!TryFindDirectory(PathUtil.TrimEndSeparator(parent), out p)) return -1;
        var name = Path.GetFileName(fullPath).ToLowerInvariant().AsSpan();
        for (int i = CountInternal - 1; i > 0; i--)
            if (Parent[i] == p && !IsDeleted(i) && GetLowerName(i).SequenceEqual(name)) return i;
        return -1;
    }

    /// <summary>Remove a path (and, for folders, everything below it). Returns removed count. Caller holds the write lock.</summary>
    internal int RemovePath(string fullPath)
    {
        int i = FindPath(fullPath);
        return i > 0 ? DeleteSubtree(i) : 0;
    }

    // ------------------------------------------------------------------ writing (caller holds write lock)

    internal void EnsureCapacity(int extraEntries)
    {
        int need = CountInternal + extraEntries;
        if (need <= Parent.Length) return;
        int cap = Math.Max(need, (int)Math.Min((long)Parent.Length * 3 / 2, Array.MaxLength - 1));
        Array.Resize(ref Parent, cap);
        Array.Resize(ref NameOffset, cap + 1);
        Array.Resize(ref NameLength, cap);
        Array.Resize(ref Flags, cap);
        if (Frn != null) Array.Resize(ref Frn, cap);
    }

    internal int AppendRaw(int parent, ReadOnlySpan<char> name, IndexEntryFlags flags, ulong key)
    {
        if (name.Length > 255) name = name[..255];
        EnsureCapacity(1);
        int i = CountInternal;
        int off = Names.Append(name);
        Lower.EnsureCapacity(name.Length + 1);
        NameArena.LowerInto(name, Lower.Data.AsSpan(off, name.Length));
        Lower.Data[off + name.Length] = '\0';
        Lower.Length = Names.Length;
        Parent[i] = parent;
        NameOffset[i] = off;
        NameOffset[i + 1] = Names.Length;
        NameLength[i] = (byte)name.Length;
        if (PinyinHelper.ContainsCjk(name)) flags |= IndexEntryFlags.HasCjk;
        Flags[i] = flags;
        if (Frn != null)
        {
            Frn[i] = key;
            if (i >= BaseCount && i > 0) Overflow![key] = i;
        }
        CountInternal = i + 1;
        if ((flags & IndexEntryFlags.HasCjk) != 0 && Pinyin != null) Pinyin.Add(i, name);
        Dirty = true;
        return i;
    }

    internal void MarkDeleted(int i)
    {
        if (i <= 0 || (Flags[i] & IndexEntryFlags.Deleted) != 0) return;
        Flags[i] |= IndexEntryFlags.Deleted;
        DeletedCount++;
        GarbageChars += NameLength[i] + 1;
        if (Frn != null && Overflow != null && Overflow.TryGetValue(Frn[i], out int o) && o == i) Overflow.Remove(Frn[i]);
        if ((Flags[i] & IndexEntryFlags.Directory) != 0)
        {
            _dirPathCache.TryRemove(i, out _);
            if (DirPaths != null)
            {
                // Remove by value is O(n); acceptable for rare dir deletes. Do it via path.
                var p = GetFullPathUnsafeForRemoval(i);
                if (p != null) DirPaths.Remove(p);
            }
        }
        Dirty = true;
    }

    private string? GetFullPathUnsafeForRemoval(int i)
    {
        try { return PathUtil.TrimEndSeparator(GetFullPath(i)); } catch { return null; }
    }

    /// <summary>Mark an entry and everything below it as deleted. O(n) for directories.</summary>
    internal int DeleteSubtree(int root)
    {
        if (root <= 0) return 0;
        if (!IsDirectory(root)) { MarkDeleted(root); return 1; }
        // state: 0 unknown, 1 inside, 2 outside
        var state = new byte[CountInternal];
        state[root] = 1;
        state[0] = 2;
        int removed = 0;
        var stack = new Stack<int>();
        for (int i = 1; i < CountInternal; i++)
        {
            if ((Flags[i] & IndexEntryFlags.Deleted) != 0 && i != root) continue;
            int j = i;
            while (state[j] == 0) { stack.Push(j); j = Parent[j] < 0 ? 0 : Parent[j]; }
            byte s = state[j];
            while (stack.Count > 0) state[stack.Pop()] = s;
        }
        for (int i = 1; i < CountInternal; i++)
        {
            if (state[i] == 1 && (Flags[i] & IndexEntryFlags.Deleted) == 0) { MarkDeleted(i); removed++; }
        }
        _dirPathCache.Clear();
        return removed;
    }

    /// <summary>Rename/move. Shorter-or-equal names are overwritten in place; longer names append a new entry.</summary>
    internal int RenameOrMove(int i, int newParent, ReadOnlySpan<char> newName, IndexEntryFlags? newFlags)
    {
        bool isDir = IsDirectory(i);
        string? oldDirPath = isDir && DirPaths != null ? PathUtil.TrimEndSeparator(GetFullPath(i)) : null;
        if (newName.Length > 255) newName = newName[..255];
        int result = i;
        var flags = (newFlags ?? Flags[i]) & ~(IndexEntryFlags.HasCjk | IndexEntryFlags.Deleted);
        if (newName.Length <= NameLength[i])
        {
            int off = NameOffset[i];
            int oldLen = NameLength[i];
            newName.CopyTo(Names.Data.AsSpan(off));
            NameArena.LowerInto(newName, Lower.Data.AsSpan(off, newName.Length));
            for (int k = newName.Length; k < oldLen; k++) { Names.Data[off + k] = '\0'; Lower.Data[off + k] = '\0'; }
            GarbageChars += oldLen - newName.Length;
            NameLength[i] = (byte)newName.Length;
            Parent[i] = newParent;
            bool cjk = PinyinHelper.ContainsCjk(newName);
            Flags[i] = flags | (cjk ? IndexEntryFlags.HasCjk : 0);
            if (cjk && Pinyin != null) Pinyin.Add(i, newName); // stale slot for old name is filtered by name check in search
        }
        else
        {
            ulong key = Frn != null ? Frn[i] : 0;
            // Mark old deleted without removing descendants; re-parent children.
            Flags[i] |= IndexEntryFlags.Deleted;
            DeletedCount++;
            GarbageChars += NameLength[i] + 1;
            result = AppendRaw(newParent, newName, flags, key);
            if (isDir)
            {
                for (int k = 1; k < CountInternal; k++)
                    if (Parent[k] == i) Parent[k] = result;
            }
        }
        if (isDir)
        {
            _dirPathCache.Clear();
            if (DirPaths != null)
            {
                if (oldDirPath != null) RemoveDirPathsUnder(oldDirPath);
                RegisterDirPathsUnder(result);
            }
        }
        Dirty = true;
        return result;
    }

    private void RemoveDirPathsUnder(string oldDirPath)
    {
        if (DirPaths == null) return;
        // Descendant keys become stale; lookups verify the reconstructed path, so they simply miss.
        DirPaths.Remove(oldDirPath);
    }

    private void RegisterDirPathsUnder(int dir)
    {
        if (DirPaths == null) return;
        DirPaths[PathUtil.TrimEndSeparator(GetFullPath(dir))] = dir;
        // Register descendants (O(n) scan; directory moves are rare)
        var inside = new byte[CountInternal];
        inside[dir] = 1;
        for (int k = 1; k < CountInternal; k++)
        {
            if (!IsDirectory(k) || IsDeleted(k) || k == dir) continue;
            int j = k, guard = 0;
            while (j > 0 && inside[j] == 0 && guard++ < 256) j = Parent[j];
            if (inside[j] == 1) DirPaths[PathUtil.TrimEndSeparator(GetFullPath(k))] = k;
        }
    }

    internal void SetAttributes(int i, IndexEntryFlags newFlags)
    {
        var keep = Flags[i] & (IndexEntryFlags.HasCjk | IndexEntryFlags.Deleted);
        Flags[i] = (newFlags & ~(IndexEntryFlags.HasCjk | IndexEntryFlags.Deleted)) | keep;
        Dirty = true;
    }

    internal void RegisterDirectory(int i)
    {
        if (DirPaths != null && IsDirectory(i)) DirPaths[PathUtil.TrimEndSeparator(GetFullPath(i))] = i;
    }

    internal void ClearPathCache() => _dirPathCache.Clear();

    // ------------------------------------------------------------------ hard links (USN)

    /// <summary>Recompute <see cref="LinkedBases"/> from the keys (after build, load or compaction).</summary>
    internal void RebuildLinkMap()
    {
        if (Frn == null) return;
        Dictionary<ulong, byte>? map = null;
        for (int i = 1; i < CountInternal; i++)
        {
            ulong k = Frn[i];
            if (!LinkKey.IsExtra(k) || (Flags[i] & IndexEntryFlags.Deleted) != 0) continue;
            map ??= new Dictionary<ulong, byte>();
            ulong b = LinkKey.Base(k);
            byte ord = (byte)((k >> 40) & 0x7F);
            if (!map.TryGetValue(b, out var max) || ord > max) map[b] = ord;
        }
        LinkedBases = map;
    }

    /// <summary>Live entries for all names of file <paramref name="frn"/> (its own key plus extra hard-link keys).</summary>
    internal void GetLinkFamily(ulong frn, List<int> result)
    {
        result.Clear();
        if (TryFindByKey(frn, out int b)) result.Add(b);
        if (LinkedBases == null || !LinkedBases.TryGetValue(frn, out var max)) return;
        for (int o = 1; o <= max; o++)
            if (TryFindByKey(LinkKey.Extra(frn, o), out int i)) result.Add(i);
    }

    /// <summary>Add another name of a hard-linked file under the first free link key.</summary>
    internal int AddLink(ulong frn, int parent, ReadOnlySpan<char> name, IndexEntryFlags flags)
    {
        if (!TryFindByKey(frn, out _)) return AppendRaw(parent, name, flags, frn);
        for (int o = 1; o <= LinkKey.MaxExtra; o++)
        {
            ulong key = LinkKey.Extra(frn, o);
            if (TryFindByKey(key, out _)) continue;
            LinkedBases ??= new Dictionary<ulong, byte>();
            if (!LinkedBases.TryGetValue(frn, out var max) || o > max) LinkedBases[frn] = (byte)o;
            return AppendRaw(parent, name, flags, key);
        }
        return -1;
    }

    // ------------------------------------------------------------------ derived data

    /// <summary>(Re)build the pinyin initials arena for CJK entries.</summary>
    internal void BuildPinyin()
    {
        var p = new PinyinArena();
        for (int i = 1; i < CountInternal; i++)
        {
            if ((Flags[i] & (IndexEntryFlags.HasCjk | IndexEntryFlags.Deleted)) == IndexEntryFlags.HasCjk)
                p.Add(i, GetName(i));
        }
        Pinyin = p;
        PinyinVersion = PinyinInitialsTable.Version;
    }

    /// <summary>Rebuild DirPaths for crawler volumes (after load/compaction).</summary>
    internal void BuildDirPaths()
    {
        if (DirPaths == null) return;
        DirPaths.Clear();
        DirPaths[PathUtil.TrimEndSeparator(RootPath)] = 0;
        // Compute directory paths top-down with memo.
        var paths = new string?[CountInternal];
        paths[0] = PathUtil.TrimEndSeparator(RootPath);
        var stack = new Stack<int>();
        for (int i = 1; i < CountInternal; i++)
        {
            if (!IsDirectory(i) || IsDeleted(i) || paths[i] != null) continue;
            int j = i;
            while (j > 0 && paths[j] == null) { stack.Push(j); j = Parent[j]; if (j < 0) break; }
            if (j < 0) { stack.Clear(); continue; }
            while (stack.Count > 0)
            {
                int k = stack.Pop();
                var parent = paths[Parent[k]]!;
                paths[k] = parent.EndsWith('\\') ? parent + GetName(k).ToString() : parent + "\\" + GetName(k).ToString();
                if (IsDirectory(k)) DirPaths[paths[k]!] = k;
            }
        }
    }

    /// <summary>Remove entries that are excluded by <paramref name="m"/> (attribute/name/path rules). Returns removed count.</summary>
    internal int Prune(ExclusionMatcher m)
    {
        int n = CountInternal;
        var state = new byte[n]; // 0 unknown, 1 keep, 2 excluded
        state[0] = 1;
        var dirPath = new string?[n];
        dirPath[0] = RootPath;
        var stack = new Stack<int>();
        int removed = 0;
        for (int i = 1; i < n; i++)
        {
            if ((Flags[i] & IndexEntryFlags.Deleted) != 0) { state[i] = 2; continue; }
            if (state[i] != 0) continue;
            int j = i;
            while (j > 0 && state[j] == 0) { stack.Push(j); j = Parent[j]; if (j < 0) { j = 0; break; } }
            byte ps = state[j];
            while (stack.Count > 0)
            {
                int k = stack.Pop();
                if (ps == 2 || (Flags[k] & IndexEntryFlags.Deleted) != 0) { state[k] = 2; ps = 2; continue; }
                bool dir = (Flags[k] & IndexEntryFlags.Directory) != 0;
                var parentPath = dirPath[Parent[k]];
                bool ex = m.IsExcludedEntry(GetName(k), parentPath, dir, FlagsUtil.ToAttributes(Flags[k]));
                state[k] = ex ? (byte)2 : (byte)1;
                if (!ex && dir && parentPath != null)
                    dirPath[k] = parentPath.EndsWith('\\') ? parentPath + GetName(k).ToString() : parentPath + "\\" + GetName(k).ToString();
                ps = state[k];
            }
        }
        for (int i = 1; i < n; i++)
        {
            if (state[i] == 2 && (Flags[i] & IndexEntryFlags.Deleted) == 0) { MarkDeleted(i); removed++; }
        }
        if (removed > 0) _dirPathCache.Clear();
        return removed;
    }

    /// <summary>Should we compact before saving?</summary>
    internal bool ShouldCompact => GarbageChars > Names.Length / 5 || DeletedCount > CountInternal / 10;

    /// <summary>Return a compacted copy (USN: re-sorted by FRN so the KeyMap is fully sorted again).</summary>
    internal VolumeIndex Compact()
    {
        int n = CountInternal;
        // Deleted parents imply deleted children.
        var live = new List<int>(n - DeletedCount);
        var alive = new bool[n];
        alive[0] = true;
        // Resolve liveness top-down: an entry is alive if not deleted and its parent is alive.
        var state = new byte[n];
        state[0] = 1;
        var stack = new Stack<int>();
        for (int i = 1; i < n; i++)
        {
            if (state[i] != 0) continue;
            int j = i;
            while (j > 0 && state[j] == 0) { stack.Push(j); j = Parent[j]; if (j < 0) { j = 0; break; } }
            byte ps = state[j];
            while (stack.Count > 0)
            {
                int k = stack.Pop();
                state[k] = ps == 1 && (Flags[k] & IndexEntryFlags.Deleted) == 0 ? (byte)1 : (byte)2;
                ps = state[k];
            }
        }
        for (int i = 1; i < n; i++) if (state[i] == 1) live.Add(i);
        if (Frn != null) live.Sort((a, b) => Frn[a].CompareTo(Frn[b]));

        int arenaNeeded = 1 + NameLength[0] + 1;
        foreach (var i in live) arenaNeeded += NameLength[i] + 1;
        var v = new VolumeIndex(RootPath, Kind, live.Count + 1 + 1024, arenaNeeded + 4096)
        {
            VolumeSerial = VolumeSerial, JournalId = JournalId, NextUsn = NextUsn, LastUpdatedUtc = LastUpdatedUtc, LastBuildTime = LastBuildTime,
        };
        var map = new int[n];
        Array.Fill(map, -1);
        map[0] = 0;
        for (int k = 0; k < live.Count; k++) map[live[k]] = k + 1;
        v.AppendRawNoIndex(-1, GetName(0), Flags[0], Frn?[0] ?? 0);
        foreach (var i in live)
            v.AppendRawNoIndex(map[Parent[i]], GetName(i), Flags[i], Frn?[i] ?? 0);
        v.BaseCount = v.CountInternal;
        v.Overflow?.Clear();
        v.BuildPinyin();
        v.BuildDirPaths();
        v.RebuildLinkMap();
        return v;
    }

    /// <summary>Append without overflow registration or pinyin (bulk building).</summary>
    internal void AppendRawNoIndex(int parent, ReadOnlySpan<char> name, IndexEntryFlags flags, ulong key)
    {
        EnsureCapacity(1);
        int i = CountInternal;
        int off = Names.Append(name);
        Lower.EnsureCapacity(name.Length + 1);
        NameArena.LowerInto(name, Lower.Data.AsSpan(off, name.Length));
        Lower.Data[off + name.Length] = '\0';
        Lower.Length = Names.Length;
        Parent[i] = parent;
        NameOffset[i] = off;
        NameOffset[i + 1] = Names.Length;
        NameLength[i] = (byte)name.Length;
        Flags[i] = (flags & ~IndexEntryFlags.HasCjk) | (PinyinHelper.ContainsCjk(name) ? IndexEntryFlags.HasCjk : 0);
        if (Frn != null) Frn[i] = key;
        CountInternal = i + 1;
    }

    /// <summary>Construct from raw arrays (persistence / builder). Lower and pinyin are derived here.</summary>
    internal static VolumeIndex FromArrays(string root, IndexSourceKind kind, int count, int[] parent, int[] nameOffset, byte[] nameLength,
        IndexEntryFlags[] flags, ulong[]? frn, char[] names, int arenaLength)
    {
        var v = new VolumeIndex(root, kind, 16, 16)
        {
            Parent = parent,
            NameOffset = nameOffset,
            NameLength = nameLength,
            Flags = flags,
            Frn = frn,
            CountInternal = count,
            BaseCount = count,
            Names = new NameArena(names, arenaLength),
        };
        if (v.NameOffset.Length < count + 1) Array.Resize(ref v.NameOffset, count + 1);
        v.NameOffset[count] = arenaLength;
        v.Lower = new NameArena(NameArena.BuildLower(names, arenaLength), arenaLength);
        if (kind == IndexSourceKind.Usn && frn != null)
        {
            // Longest sorted prefix is binary-searchable; anything appended later goes to the overflow map.
            v.Overflow = new Dictionary<ulong, int>();
            int b = Math.Min(2, count);
            while (b < count && frn[b] > frn[b - 1]) b++;
            v.BaseCount = b;
            for (int i = b; i < count; i++)
                if ((flags[i] & IndexEntryFlags.Deleted) == 0) v.Overflow[frn[i]] = i;
        }
        int deleted = 0;
        for (int i = 1; i < count; i++) if ((flags[i] & IndexEntryFlags.Deleted) != 0) deleted++;
        v.DeletedCount = deleted;
        v.BuildPinyin();
        v.BuildDirPaths();
        v.RebuildLinkMap();
        return v;
    }
}

using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Enumeration;
using Hopkey.Core.Exclusion;
using Hopkey.Core.Util;

namespace Hopkey.Core.Indexing;

/// <summary>
/// Parallel directory crawler. Excluded directories are pruned before being queued (whole subtree skipped).
/// Reparse-point directories are recorded but not recursed. Names are copied straight from the enumeration
/// span into worker-local arenas (no per-entry strings); indices are allocated globally and merged at the end
/// so that NameOffset increases with index.
/// </summary>
internal sealed class DirectoryCrawler
{
    private sealed class Worker
    {
        public char[] Arena = new char[1 << 16];
        public int ArenaLen;
        public List<(int Index, int Parent, int Off, byte Len, IndexEntryFlags Flags)> Entries = new(4096);

        public int AddName(ReadOnlySpan<char> name)
        {
            if (ArenaLen + name.Length > Arena.Length) Array.Resize(ref Arena, Math.Max(Arena.Length * 2, ArenaLen + name.Length + 256));
            int off = ArenaLen;
            name.CopyTo(Arena.AsSpan(off));
            ArenaLen += name.Length;
            return off;
        }
    }

    private readonly record struct DirWork(int Index, string Path);

    private static readonly EnumerationOptions s_options = new()
    {
        IgnoreInaccessible = true,
        AttributesToSkip = 0,
        RecurseSubdirectories = false,
        ReturnSpecialDirectories = false,
        BufferSize = 64 * 1024,
    };

    private readonly ExclusionMatcher _matcher;
    private int _nextIndex;
    private long _found;

    public DirectoryCrawler(ExclusionMatcher matcher) => _matcher = matcher;

    /// <summary>Crawl <paramref name="root"/> into a new VolumeIndex.</summary>
    public VolumeIndex Crawl(string root, IProgress<long>? progress, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        root = PathUtil.TrimEndSeparator(root);
        _nextIndex = 1; // 0 = root
        var queue = new ConcurrentStack<DirWork>();
        queue.Push(new DirWork(0, root));
        int pending = 1;
        int workerCount = Math.Clamp(Environment.ProcessorCount, 2, 8);
        var workers = new Worker[workerCount];
        var threads = new Thread[workerCount];
        Exception? failure = null;

        for (int w = 0; w < workerCount; w++)
        {
            var worker = workers[w] = new Worker();
            threads[w] = new Thread(() =>
            {
                try
                {
                    var spin = new SpinWait();
                    while (Volatile.Read(ref pending) > 0 && !ct.IsCancellationRequested)
                    {
                        if (!queue.TryPop(out var dir))
                        {
                            spin.SpinOnce(sleep1Threshold: 20);
                            continue;
                        }
                        spin.Reset();
                        try { EnumerateDirectory(dir, worker, queue, ref pending); }
                        finally { Interlocked.Decrement(ref pending); }
                        if (progress != null && (Interlocked.Read(ref _found) & 0x3FFF) < 64) progress.Report(Interlocked.Read(ref _found));
                    }
                }
                catch (Exception ex) { failure = ex; }
            }) { IsBackground = true, Name = "CL-Crawl", Priority = ThreadPriority.BelowNormal };
            threads[w].Start();
        }
        foreach (var t in threads) t.Join();
        ct.ThrowIfCancellationRequested();
        if (failure != null) throw failure;

        // Merge in index order.
        int count = _nextIndex;
        var parent = new int[count + 1024];
        var nameOffset = new int[count + 1024 + 1];
        var nameLen = new byte[count + 1024];
        var flags = new IndexEntryFlags[count + 1024];
        long arenaTotal = 1 + root.Length + 1;
        foreach (var w in workers) arenaTotal += w.ArenaLen + w.Entries.Count;
        var arena = new char[arenaTotal + 16 * 1024];
        // Gather (index → worker, slot)
        var srcWorker = new byte[count];
        var srcSlot = new int[count];
        for (int w = 0; w < workers.Length; w++)
        {
            var list = workers[w].Entries;
            for (int s = 0; s < list.Count; s++) { srcWorker[list[s].Index] = (byte)w; srcSlot[list[s].Index] = s; }
        }
        int pos = 0;
        arena[pos++] = '\0';
        // root
        var rootName = root.TrimEnd('\\');
        parent[0] = -1;
        nameOffset[0] = pos;
        rootName.AsSpan().CopyTo(arena.AsSpan(pos));
        nameLen[0] = (byte)Math.Min(255, rootName.Length);
        pos += rootName.Length;
        arena[pos++] = '\0';
        flags[0] = IndexEntryFlags.Directory;
        for (int i = 1; i < count; i++)
        {
            var w = workers[srcWorker[i]];
            var e = w.Entries[srcSlot[i]];
            parent[i] = e.Parent;
            nameOffset[i] = pos;
            nameLen[i] = e.Len;
            Array.Copy(w.Arena, e.Off, arena, pos, e.Len);
            pos += e.Len;
            arena[pos++] = '\0';
            flags[i] = e.Flags | (Pinyin.PinyinHelper.ContainsCjk(new ReadOnlySpan<char>(arena, nameOffset[i], e.Len)) ? IndexEntryFlags.HasCjk : 0);
        }
        var v = VolumeIndex.FromArrays(PathUtil.TrimEndSeparator(root.Length == 2 ? root + "\\" : root), IndexSourceKind.Crawler,
            count, parent, nameOffset, nameLen, flags, null, arena, pos);
        v.LastBuildTime = sw.Elapsed;
        v.LastUpdatedUtc = DateTime.UtcNow;
        Logger.Info($"Crawl {root}: {count - 1:N0} entries in {sw.ElapsedMilliseconds}ms ({workerCount} workers)");
        return v;
    }

    private void EnumerateDirectory(DirWork dir, Worker worker, ConcurrentStack<DirWork> queue, ref int pending)
    {
        var dirPath = dir.Path.Length == 2 ? dir.Path + "\\" : dir.Path;
        FileSystemEnumerable<bool> en;
        try
        {
            int parentIndex = dir.Index;
            var matcher = _matcher;
            var subdirs = new List<(int, string)>();
            en = new FileSystemEnumerable<bool>(dirPath, (ref FileSystemEntry e) =>
            {
                var name = e.FileName;
                var attrs = e.Attributes;
                bool isDir = e.IsDirectory;
                if (matcher.IsExcludedEntry(name, dir.Path, isDir, attrs)) return false;
                int idx = Interlocked.Increment(ref _nextIndex) - 1;
                int off = worker.AddName(name);
                var f = FlagsUtil.FromAttributes(attrs);
                worker.Entries.Add((idx, parentIndex, off, (byte)Math.Min(255, name.Length), f));
                Interlocked.Increment(ref _found);
                if (isDir)
                {
                    var full = dir.Path.EndsWith('\\') ? string.Concat(dir.Path.AsSpan(), name) : string.Concat(dir.Path.AsSpan(), "\\", name);
                    if ((attrs & FileAttributes.ReparsePoint) == 0 || IsCloudPlaceholderDirectory(full)) subdirs.Add((idx, full));
                }
                return true;
            }, s_options);
            foreach (var _ in en) { }
            if (subdirs.Count > 0)
            {
                Interlocked.Add(ref pending, subdirs.Count);
                foreach (var (i, p) in subdirs) queue.Push(new DirWork(i, p));
            }
        }
        catch (UnauthorizedAccessException) { }
        catch (DirectoryNotFoundException) { }
        catch (IOException ex) { Logger.Debug($"Crawl IO error in {dirPath}: {ex.Message}"); }
    }

    /// <summary>
    /// Reparse-point directories are either links (symlinks/junctions → skipped to avoid cycles and duplicates) or
    /// cloud-file placeholders (OneDrive/Dropbox Files On-Demand → real folders that must be indexed).
    /// Links expose a LinkTarget; cloud placeholders don't. Only called for reparse directories, so the extra syscall is rare.
    /// </summary>
    internal static bool IsCloudPlaceholderDirectory(string path)
    {
        uint tag = Native.Kernel32.GetReparseTag(path, out uint attrs);
        // IO_REPARSE_TAG_CLOUD, IO_REPARSE_TAG_CLOUD_1..F (0x9000x01A)
        bool cloud = (tag & 0xFFFF0FFF) == 0x9000001A;
        // FILE_ATTRIBUTE_RECALL_ON_OPEN: folder content isn't local; listing it would fetch from the network.
        const uint RecallOnOpen = 0x00040000;
        return cloud && (attrs & RecallOnOpen) == 0;
    }

    /// <summary>Enumerate a subtree (used for live updates). Yields (relative parent path, name, attrs) in parent-first order.</summary>
    public static IEnumerable<(string ParentPath, string Name, FileAttributes Attrs)> EnumerateSubtree(string dir, ExclusionMatcher m)
    {
        var stack = new Stack<string>();
        stack.Push(dir);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            List<(string, FileAttributes)> items = new();
            try
            {
                var en = new FileSystemEnumerable<(string, FileAttributes)>(current, (ref FileSystemEntry e) => (e.FileName.ToString(), e.Attributes), s_options);
                foreach (var it in en) items.Add(it);
            }
            catch { continue; }
            foreach (var (name, attrs) in items)
            {
                bool isDir = (attrs & FileAttributes.Directory) != 0;
                if (m.IsExcludedEntry(name, current, isDir, attrs)) continue;
                yield return (current, name, attrs);
                var child = Path.Combine(current, name);
                if (isDir && ((attrs & FileAttributes.ReparsePoint) == 0 || IsCloudPlaceholderDirectory(child))) stack.Push(child);
            }
        }
    }
}

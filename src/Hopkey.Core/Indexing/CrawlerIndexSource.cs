using System.Threading.Channels;
using Hopkey.Core.Exclusion;
using Hopkey.Core.Util;

namespace Hopkey.Core.Indexing;

/// <summary>Non-elevated / non-NTFS source: parallel crawl + FileSystemWatcher.</summary>
public sealed class CrawlerIndexSource : IIndexSource
{
    private readonly string _root;
    private FileSystemWatcher? _watcher;
    private ChannelWriter<(string Root, IndexChange Change)>? _sink;

    public string RootPath => _root;
    public IndexSourceKind Kind => IndexSourceKind.Crawler;
    public event Action<string>? RebuildRequired;

    public CrawlerIndexSource(string root) => _root = PathUtil.TrimEndSeparator(root.Length == 2 ? root + "\\" : root);

    public Task<VolumeIndex> BuildAsync(ExclusionMatcher matcher, IProgress<long>? progress, CancellationToken ct) =>
        Task.Factory.StartNew(() => new DirectoryCrawler(matcher).Crawl(_root, progress, ct), ct, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    public Task<bool> CatchUpAsync(VolumeIndex cached, ExclusionMatcher matcher, CancellationToken ct) => Task.FromResult(false);

    public void StartWatching(ChannelWriter<(string Root, IndexChange Change)> sink)
    {
        StopWatching();
        _sink = sink;
        try
        {
            var w = new FileSystemWatcher(PathUtil.EnsureEndSeparator(_root))
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Attributes,
                InternalBufferSize = 64 * 1024,
            };
            w.Created += (_, e) => Post(new IndexChange(ChangeKind.Created, 0, 0, null, 0, e.FullPath, null));
            w.Deleted += (_, e) => Post(new IndexChange(ChangeKind.Deleted, 0, 0, null, 0, e.FullPath, null));
            w.Renamed += (_, e) => Post(new IndexChange(ChangeKind.Renamed, 0, 0, null, 0, e.FullPath, e.OldFullPath));
            w.Changed += (_, e) => Post(new IndexChange(ChangeKind.AttributesChanged, 0, 0, null, 0, e.FullPath, null));
            w.Error += (_, e) =>
            {
                Logger.Warn($"FileSystemWatcher error on {_root}: {e.GetException().Message}");
                RebuildRequired?.Invoke(_root);
            };
            w.EnableRaisingEvents = true;
            _watcher = w;
        }
        catch (Exception ex)
        {
            Logger.Warn($"Cannot watch {_root}", ex);
        }
    }

    private void Post(IndexChange c) => _sink?.TryWrite((_root, c));

    public void StopWatching()
    {
        if (_watcher != null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Dispose();
            _watcher = null;
        }
    }

    public void Dispose() => StopWatching();
}

/// <summary>Applies path-based (FSW) changes to a crawler volume. Caller holds the write lock.</summary>
internal static class PathChangeApplier
{
    public static void Apply(VolumeIndex v, IReadOnlyList<IndexChange> changes, ExclusionMatcher m)
    {
        // One O(n) pass builds a (parent, lowerName) → index map for all parents touched by this batch.
        var parents = new HashSet<int>();
        foreach (var c in changes)
        {
            AddParent(v, c.FullPath, parents);
            AddParent(v, c.OldFullPath, parents);
        }
        var childMap = BuildChildMap(v, parents);

        foreach (var c in changes)
        {
            try { ApplyOne(v, c, m, childMap); }
            catch (Exception ex) { Logger.Warn($"Failed to apply FSW change {c.Kind} {c.FullPath}", ex); }
        }
        v.LastUpdatedUtc = DateTime.UtcNow;
    }

    private static void AddParent(VolumeIndex v, string? path, HashSet<int> parents)
    {
        if (path == null) return;
        var parent = PathUtil.GetParent(path);
        if (parent != null && v.TryFindDirectory(parent, out int p)) parents.Add(p);
    }

    private static Dictionary<(int, string), int> BuildChildMap(VolumeIndex v, HashSet<int> parents)
    {
        var map = new Dictionary<(int, string), int>();
        if (parents.Count == 0) return map;
        for (int i = 1; i < v.CountInternal; i++)
        {
            if ((v.Flags[i] & IndexEntryFlags.Deleted) != 0) continue;
            if (parents.Contains(v.Parent[i])) map[(v.Parent[i], v.GetLowerName(i).ToString())] = i;
        }
        return map;
    }

    private static bool TryFind(VolumeIndex v, string path, Dictionary<(int, string), int> childMap, out int index)
    {
        if (v.TryFindDirectory(path, out index)) return true;
        var parent = PathUtil.GetParent(path);
        index = -1;
        if (parent == null || !v.TryFindDirectory(parent, out int p)) return false;
        var name = Path.GetFileName(path).ToLowerInvariant();
        if (childMap.TryGetValue((p, name), out index) && !v.IsDeleted(index)) return true;
        // Fallback scan (entries created in this batch)
        for (int i = v.CountInternal - 1; i > 0; i--)
        {
            if (v.Parent[i] == p && !v.IsDeleted(i) && v.GetLowerName(i).SequenceEqual(name)) { index = i; return true; }
        }
        return false;
    }

    private static void ApplyOne(VolumeIndex v, IndexChange c, ExclusionMatcher m, Dictionary<(int, string), int> childMap)
    {
        var path = c.FullPath;
        if (path == null) return;
        switch (c.Kind)
        {
            case ChangeKind.Created:
                Create(v, path, m, childMap);
                break;
            case ChangeKind.Deleted:
                if (TryFind(v, path, childMap, out int del)) v.DeleteSubtree(del);
                break;
            case ChangeKind.Renamed:
            {
                bool had = c.OldFullPath != null && TryFind(v, c.OldFullPath, childMap, out int old) && Rename(v, old, path, m);
                if (!had) Create(v, path, m, childMap);
                break;
            }
            case ChangeKind.AttributesChanged:
            {
                if (!TryFind(v, path, childMap, out int idx)) return;
                FileAttributes attrs;
                try { attrs = File.GetAttributes(path); } catch { return; }
                v.SetAttributes(idx, FlagsUtil.FromAttributes(attrs));
                var parentPath = PathUtil.GetParent(path);
                if (m.IsExcludedEntry(Path.GetFileName(path), parentPath, (attrs & FileAttributes.Directory) != 0, attrs)) v.DeleteSubtree(idx);
                break;
            }
        }
    }

    private static bool Rename(VolumeIndex v, int idx, string newPath, ExclusionMatcher m)
    {
        var parentPath = PathUtil.GetParent(newPath);
        // Same rule as Create: the parent is indexed (so its ancestors were accepted); only this level is checked.
        if (parentPath == null || !v.TryFindDirectory(parentPath, out int parent) ||
            m.IsExcludedEntry(Path.GetFileName(newPath), parentPath, v.IsDirectory(idx), FlagsUtil.ToAttributes(v.Flags[idx])))
        {
            v.DeleteSubtree(idx);
            return true;
        }
        v.RenameOrMove(idx, parent, Path.GetFileName(newPath), null);
        return true;
    }

    private static void Create(VolumeIndex v, string path, ExclusionMatcher m, Dictionary<(int, string), int> childMap)
    {
        var parentPath = PathUtil.GetParent(path);
        if (parentPath == null || !v.TryFindDirectory(parentPath, out int parent)) return;
        if (TryFind(v, path, childMap, out _)) return;
        FileAttributes attrs;
        try { attrs = File.GetAttributes(path); } catch { return; }
        bool isDir = (attrs & FileAttributes.Directory) != 0;
        var name = Path.GetFileName(path);
        if (m.IsExcludedEntry(name, parentPath, isDir, attrs)) return;
        int idx = v.AppendRaw(parent, name, FlagsUtil.FromAttributes(attrs), 0);
        if (!isDir) return;
        v.RegisterDirectory(idx);
        if ((attrs & FileAttributes.ReparsePoint) != 0 && !DirectoryCrawler.IsCloudPlaceholderDirectory(path)) return;
        // Directory moved/copied in: index its existing content.
        foreach (var (pp, n, a) in DirectoryCrawler.EnumerateSubtree(path, m))
        {
            if (!v.TryFindDirectory(pp, out int p)) continue;
            int child = v.AppendRaw(p, n, FlagsUtil.FromAttributes(a), 0);
            if ((a & FileAttributes.Directory) != 0) v.RegisterDirectory(child);
        }
    }
}

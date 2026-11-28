using Hopkey.Core.Util;

namespace Hopkey.Core.Indexing;

/// <summary>All volume indexes. The array is replaced copy-on-write; readers just grab the reference.</summary>
public sealed class FileIndex
{
    private volatile VolumeIndex[] _volumes = Array.Empty<VolumeIndex>();
    private readonly Timer _changedTimer;
    private int _changedPending;

    public FileIndex()
    {
        _changedTimer = new Timer(_ =>
        {
            Interlocked.Exchange(ref _changedPending, 0);
            try { Changed?.Invoke(); } catch (Exception ex) { Logger.Error("FileIndex.Changed handler failed", ex); }
        }, null, Timeout.Infinite, Timeout.Infinite);
    }

    public IReadOnlyList<VolumeIndex> Volumes => _volumes;

    internal VolumeIndex[] VolumesArray => _volumes;

    public long TotalLiveCount
    {
        get
        {
            long n = 0;
            foreach (var v in _volumes) n += v.LiveCount;
            return n;
        }
    }

    /// <summary>Raised (throttled to 1s) when entries change.</summary>
    public event Action? Changed;

    /// <summary>
    /// Raised synchronously when the volume array is swapped (build, rebuild, compaction, root removal). Holders of
    /// the old array must drop it here, or the replaced volumes (hundreds of MB) survive the post-build trim.
    /// </summary>
    public event Action? VolumesReplaced;

    public VolumeIndex? Find(string root)
    {
        foreach (var v in _volumes)
            if (PathUtil.Equal(v.RootPath, root)) return v;
        return null;
    }

    public void SetVolume(VolumeIndex v)
    {
        lock (this)
        {
            var list = _volumes.Where(x => !PathUtil.Equal(x.RootPath, v.RootPath)).ToList();
            list.Add(v);
            list.Sort((a, b) => string.Compare(a.RootPath, b.RootPath, StringComparison.OrdinalIgnoreCase));
            _volumes = list.ToArray();
        }
        OnReplaced();
    }

    public void RemoveVolume(string root)
    {
        lock (this)
            _volumes = _volumes.Where(x => !PathUtil.Equal(x.RootPath, root)).ToArray();
        OnReplaced();
    }

    private void OnReplaced()
    {
        try { VolumesReplaced?.Invoke(); } catch (Exception ex) { Logger.Error("FileIndex.VolumesReplaced handler failed", ex); }
        RaiseChanged();
    }

    /// <summary>Drop a deleted file/folder from the index right away (the watcher catches up later). Returns removed count.</summary>
    public int RemovePath(string fullPath)
    {
        VolumeIndex? best = null;
        foreach (var v in _volumes)
            if (PathUtil.IsUnder(fullPath, v.RootPath) && (best == null || v.RootPath.Length > best.RootPath.Length)) best = v;
        if (best == null) return 0;
        int removed;
        best.Lock.EnterWriteLock();
        try { removed = best.RemovePath(fullPath); }
        finally { best.Lock.ExitWriteLock(); }
        if (removed > 0) RaiseChanged();
        return removed;
    }

    public void RaiseChanged()
    {
        if (Interlocked.Exchange(ref _changedPending, 1) == 0)
            _changedTimer.Change(1000, Timeout.Infinite);
    }
}

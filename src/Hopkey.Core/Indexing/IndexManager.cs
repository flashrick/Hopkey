using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;
using Hopkey.Core.Config;
using Hopkey.Core.Exclusion;
using Hopkey.Core.Pinyin;
using Hopkey.Core.Util;

namespace Hopkey.Core.Indexing;

/// <summary>
/// Orchestrates index sources: load cache → catch up → watch; builds when needed; applies live changes on a single
/// writer thread (300ms batches); handles exclusion/root changes incrementally; saves periodically.
/// </summary>
public sealed class IndexManager : IDisposable
{
    private readonly FileIndex _index = new();
    private readonly Channel<(string Root, IndexChange Change)> _changes =
        Channel.CreateUnbounded<(string, IndexChange)>(new UnboundedChannelOptions { SingleReader = true });
    private readonly ConcurrentDictionary<string, IIndexSource> _sources = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _builds = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Timer> _rebuildTimers = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _buildGate = new(Math.Max(2, Environment.ProcessorCount / 2));
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Func<IndexSettings> _settings;
    private volatile ExclusionMatcher _matcher;
    private Thread? _writer;
    private Timer? _saveTimer;
    private Timer? _exclusionTimer;
    private ExclusionMatcher? _pendingOldMatcher;
    private volatile bool _paused;
    private volatile bool _saveRequested;
    private IndexStatus _status = IndexStatus.Initial;
    private int _building;
    private volatile bool _startupDone;

    public FileIndex Index => _index;
    public IndexStatus Status => _status;
    public ExclusionMatcher Matcher => _matcher;
    public event Action<IndexStatus>? StatusChanged;

    public IndexManager(Func<IndexSettings> settings, ExclusionMatcher matcher)
    {
        _settings = settings;
        _matcher = matcher;
        PinyinInitialsTable.Updated += OnPinyinUpdated;
    }

    // ------------------------------------------------------------------ roots

    public static List<string> ResolveRoots(IndexSettings s)
    {
        var roots = new List<string>();
        if (s.Roots == null || s.Roots.Count == 0)
        {
            foreach (var d in DriveInfo.GetDrives())
            {
                try { if (d.DriveType == DriveType.Fixed && d.IsReady) roots.Add(d.RootDirectory.FullName); }
                catch { }
            }
        }
        else
        {
            foreach (var r in s.Roots)
            {
                var p = PathUtil.Normalize(r);
                if (p.Length == 2) p += "\\";
                if (Directory.Exists(p)) roots.Add(p);
            }
        }
        // Drop roots covered by another root.
        roots = roots.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return roots.Where(r => !roots.Any(o => !PathUtil.Equal(o, r) && PathUtil.IsUnder(r, o))).ToList();
    }

    private IIndexSource CreateSource(string root)
    {
        var s = _settings();
        bool isVolumeRoot = root.Length <= 3;
        IIndexSource src = isVolumeRoot && s.UseUsnWhenElevated && UsnIndexSource.IsSupported(root)
            ? new UsnIndexSource(root)
            : new CrawlerIndexSource(root);
        src.RebuildRequired += r => ScheduleRebuild(r, TimeSpan.FromSeconds(2));
        return src;
    }

    // ------------------------------------------------------------------ startup

    public Task StartAsync() => Task.Run(StartCoreAsync);

    private async Task StartCoreAsync()
    {
        var ct = _lifetime.Token;
        _writer = new Thread(WriterLoop) { IsBackground = true, Name = "CL-IndexWriter", Priority = ThreadPriority.BelowNormal };
        _writer.Start();
        int minutes = Math.Max(1, _settings().SaveIntervalMinutes);
        _saveTimer = new Timer(_ => _saveRequested = true, null, TimeSpan.FromMinutes(minutes), TimeSpan.FromMinutes(minutes));

        SetStatus(new IndexStatus(IndexState.Loading, 0));
        var sw = Stopwatch.StartNew();
        var cache = IndexPersistence.Load(AppPaths.IndexFile);
        if (cache != null)
            Logger.Info($"Index cache loaded: {cache.Volumes.Count} volumes, {cache.Volumes.Sum(v => (long)v.LiveCount):N0} entries in {sw.ElapsedMilliseconds}ms");

        var roots = ResolveRoots(_settings());
        var tasks = new List<Task>();
        foreach (var root in roots)
        {
            var src = CreateSource(root);
            _sources[root] = src;
            var cached = cache?.Volumes.FirstOrDefault(v => PathUtil.Equal(v.RootPath, src.RootPath) && v.Kind == src.Kind);
            tasks.Add(StartRootAsync(src, cached, cache != null && cache.Fingerprint == _matcher.Fingerprint, ct));
        }
        cache = null; // don't keep replaced cached volumes alive (async state machine field)
        UpdateReadyStatus();
        await Task.WhenAll(tasks).ConfigureAwait(false);
        // Completed async tasks keep their state machines (incl. the replaced cached volumes) alive: drop them.
        tasks.Clear();
        _startupDone = true;
        UpdateReadyStatus();
        TrimMemory("startup indexing done");
        foreach (var v in _index.VolumesArray) Logger.Info("Index memory " + v.DescribeMemory());
    }

    /// <summary>Free build-time temporaries (crawler buffers, raw MFT records, replaced volumes) and log memory.</summary>
    public static void TrimMemory(string reason)
    {
        System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        // Aggressive also decommits the free space the GC would otherwise keep committed for future allocations.
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        using var p = Process.GetCurrentProcess();
        var gi = GC.GetGCMemoryInfo();
        Logger.Info($"Memory after {reason}: managed {GC.GetTotalMemory(false) / 1048576.0:F0} MB (heap {gi.HeapSizeBytes / 1048576.0:F0}, committed {gi.TotalCommittedBytes / 1048576.0:F0}, fragmented {gi.FragmentedBytes / 1048576.0:F0}), working set {p.WorkingSet64 / 1048576.0:F0} MB, private {p.PrivateMemorySize64 / 1048576.0:F0} MB");
    }

    private async Task StartRootAsync(IIndexSource src, VolumeIndex? cached, bool fingerprintMatches, CancellationToken ct)
    {
        try
        {
            if (cached != null)
            {
                if (!fingerprintMatches)
                {
                    cached.Lock.EnterWriteLock();
                    try { cached.Prune(_matcher); } finally { cached.Lock.ExitWriteLock(); }
                }
                _index.SetVolume(cached);
                if (src is UsnIndexSource usn) usn.Target = cached;
                bool caughtUp = fingerprintMatches && await src.CatchUpAsync(cached, _matcher, ct).ConfigureAwait(false);
                if (caughtUp)
                {
                    if (!_paused) src.StartWatching(_changes.Writer);
                    return;
                }
                // Keep cache searchable; rebuild in background and swap atomically.
                cached = null; // the FileIndex holds it until the swap; don't pin it in this state machine
                await BuildRootAsync(src, ct).ConfigureAwait(false);
                return;
            }
            await BuildRootAsync(src, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Logger.Error($"Index start failed for {src.RootPath}", ex);
            SetStatus(new IndexStatus(IndexState.Error, _index.TotalLiveCount, ex.Message));
        }
    }

    private async Task BuildRootAsync(IIndexSource src, CancellationToken outer)
    {
        var root = src.RootPath;
        if (_builds.TryRemove(root, out var prev)) prev.Cancel();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
        _builds[root] = cts;
        Interlocked.Increment(ref _building);
        await _buildGate.WaitAsync(cts.Token).ConfigureAwait(false);
        try
        {
            src.StopWatching();
            SetStatus(new IndexStatus(IndexState.Building, _index.TotalLiveCount, root, _building));
            var progress = new Progress<long>(n => SetStatus(new IndexStatus(IndexState.Building, Math.Max(n, _index.TotalLiveCount), root, _building)));
            var v = await src.BuildAsync(_matcher, progress, cts.Token).ConfigureAwait(false);
            if (src is UsnIndexSource usn) usn.Target = v;
            _index.SetVolume(v);
            // Bulk-built volumes aren't marked dirty by their appends; without this SaveCore skips them whenever an
            // older index.bin exists, and the fresh build would only be written at exit.
            v.Dirty = true;
            _saveRequested = true;
            if (!_paused) src.StartWatching(_changes.Writer);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Logger.Error($"Index build failed for {root}", ex);
            SetStatus(new IndexStatus(IndexState.Error, _index.TotalLiveCount, ex.Message));
        }
        finally
        {
            _buildGate.Release();
            // Later rebuilds (not part of startup) trim on their own — once, after the last concurrent build, since
            // a trim while another volume is still building can't release that build's temporaries.
            if (Interlocked.Decrement(ref _building) == 0 && _startupDone)
                _ = Task.Run(() => TrimMemory("rebuild of " + root));
            _builds.TryRemove(new KeyValuePair<string, CancellationTokenSource>(root, cts));
            cts.Dispose();
            UpdateReadyStatus();
        }
    }

    private void UpdateReadyStatus()
    {
        if (_paused) { SetStatus(new IndexStatus(IndexState.Paused, _index.TotalLiveCount)); return; }
        if (_building > 0) { SetStatus(new IndexStatus(IndexState.Building, _index.TotalLiveCount, null, _building)); return; }
        if (_status.State == IndexState.Error) return;
        SetStatus(new IndexStatus(IndexState.Ready, _index.TotalLiveCount));
    }

    private void SetStatus(IndexStatus s)
    {
        _status = s;
        try { StatusChanged?.Invoke(s); } catch (Exception ex) { Logger.Warn("StatusChanged handler failed", ex); }
    }

    // ------------------------------------------------------------------ rebuild

    public Task RebuildAsync(string? root = null)
    {
        var targets = root == null ? _sources.Values.ToList() : _sources.Values.Where(s => PathUtil.Equal(s.RootPath, root)).ToList();
        return Task.WhenAll(targets.Select(s => BuildRootAsync(s, _lifetime.Token)));
    }

    private void ScheduleRebuild(string root, TimeSpan delay)
    {
        var t = _rebuildTimers.GetOrAdd(root, r => new Timer(_ =>
        {
            if (_sources.TryGetValue(r, out var s)) _ = BuildRootAsync(s, _lifetime.Token);
        }, null, Timeout.Infinite, Timeout.Infinite));
        t.Change(delay, Timeout.InfiniteTimeSpan);
    }

    // ------------------------------------------------------------------ live updates

    private void WriterLoop()
    {
        var reader = _changes.Reader;
        var batch = new Dictionary<string, List<IndexChange>>(StringComparer.OrdinalIgnoreCase);
        var ct = _lifetime.Token;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Wait for first item or periodic tick (for saves).
                var waitTask = reader.WaitToReadAsync(ct).AsTask();
                waitTask.Wait(1000, ct);
                var deadline = Environment.TickCount64 + 300;
                int total = 0;
                while (total < 2000)
                {
                    while (total < 2000 && reader.TryRead(out var item))
                    {
                        if (!batch.TryGetValue(item.Root, out var list)) batch[item.Root] = list = new List<IndexChange>();
                        list.Add(item.Change);
                        total++;
                    }
                    if (total == 0 || Environment.TickCount64 >= deadline) break;
                    Thread.Sleep(20);
                }
                if (total > 0) ApplyBatches(batch);
                batch.Clear();
                if (_saveRequested) { _saveRequested = false; SaveCore(); }
            }
            catch (OperationCanceledException) { break; }
            catch (AggregateException ae) when (ae.InnerException is OperationCanceledException) { break; }
            catch (Exception ex) { Logger.Error("Index writer loop error", ex); Thread.Sleep(500); }
        }
    }

    private void ApplyBatches(Dictionary<string, List<IndexChange>> batch)
    {
        var m = _matcher;
        foreach (var (root, list) in batch)
        {
            var v = _index.Find(root);
            if (v == null) continue;
            // Keep write-lock hold times short: split big batches.
            for (int start = 0; start < list.Count; start += 500)
            {
                var slice = list.GetRange(start, Math.Min(500, list.Count - start));
                v.Lock.EnterWriteLock();
                try
                {
                    if (v.Kind == IndexSourceKind.Usn) UsnChangeApplier.Apply(v, slice, m);
                    else PathChangeApplier.Apply(v, slice, m);
                }
                finally { v.Lock.ExitWriteLock(); }
            }
            if (Logger.MinLevel <= LogLevel.Debug)
                Logger.Debug($"Applied {list.Count} change(s) to {root}: " + string.Join(", ", list.Where(c => c.Kind != UsnChangeApplier.PositionMarker).Take(5).Select(c => $"{c.Kind} {c.Name ?? c.FullPath}")));
            if (v.NeedsRebuild)
            {
                v.NeedsRebuild = false;
                ScheduleRebuild(root, TimeSpan.FromSeconds(30));
            }
        }
        _index.RaiseChanged();
    }

    // ------------------------------------------------------------------ exclusion changes

    /// <summary>Called when rules change (debounced 1s). Additions prune immediately; removals restore via (partial) rebuild.</summary>
    public void OnExclusionChanged(ExclusionMatcher newMatcher)
    {
        if (newMatcher.Fingerprint == _matcher.Fingerprint) return;
        lock (this)
        {
            _pendingOldMatcher ??= _matcher;
            _matcher = newMatcher;
            _exclusionTimer ??= new Timer(_ => ProcessExclusionChange(), null, Timeout.Infinite, Timeout.Infinite);
            _exclusionTimer.Change(1000, Timeout.Infinite);
        }
    }

    private void ProcessExclusionChange()
    {
        ExclusionMatcher oldM, newM;
        lock (this)
        {
            if (_pendingOldMatcher == null) return;
            oldM = _pendingOldMatcher;
            newM = _matcher;
            _pendingOldMatcher = null;
        }
        var diff = newM.Diff(oldM);
        Logger.Info($"Exclusion change: additions={diff.HasAdditions} removals={diff.HasRemovals} removedPaths={diff.RemovedPathRules.Count}");
        var sw = Stopwatch.StartNew();
        if (diff.HasAdditions)
        {
            int removed = 0;
            foreach (var v in _index.VolumesArray)
            {
                v.Lock.EnterWriteLock();
                try { removed += v.Prune(newM); }
                finally { v.Lock.ExitWriteLock(); }
            }
            Logger.Info($"Pruned {removed:N0} entries in {sw.ElapsedMilliseconds}ms");
            _index.RaiseChanged();
            _saveRequested = true;
        }
        if (diff.HasRemovals)
        {
            foreach (var v in _index.VolumesArray)
            {
                if (!_sources.TryGetValue(v.RootPath, out var src)) continue;
                if (v.Kind == IndexSourceKind.Crawler && !diff.RemovedNonPathRules)
                {
                    foreach (var p in diff.RemovedPathRules)
                    {
                        if (!PathUtil.IsUnder(p, v.RootPath) || newM.IsExcludedPath(p, true) || !Directory.Exists(p)) continue;
                        // Posting a "Created" change re-adds the folder and crawls its subtree.
                        _changes.Writer.TryWrite((v.RootPath, new IndexChange(ChangeKind.Created, 0, 0, null, 0, p, null)));
                    }
                }
                else
                {
                    _ = BuildRootAsync(src, _lifetime.Token);
                }
            }
        }
    }

    // ------------------------------------------------------------------ roots / pause / save

    public void OnRootsChanged()
    {
        var roots = ResolveRoots(_settings());
        foreach (var existing in _sources.Keys.ToList())
        {
            if (roots.Any(r => PathUtil.Equal(r, existing))) continue;
            if (_sources.TryRemove(existing, out var s)) { s.StopWatching(); s.Dispose(); }
            if (_builds.TryRemove(existing, out var b)) b.Cancel();
            _index.RemoveVolume(existing);
        }
        foreach (var root in roots)
        {
            if (_sources.Keys.Any(k => PathUtil.Equal(k, root))) continue;
            var src = CreateSource(root);
            _sources[root] = src;
            _ = BuildRootAsync(src, _lifetime.Token);
        }
        _saveRequested = true;
    }

    /// <summary>Called when IndexSettings.UseUsnWhenElevated changes: recreate sources.</summary>
    public void OnSourceModeChanged()
    {
        foreach (var root in _sources.Keys.ToList())
        {
            if (_sources.TryRemove(root, out var s)) { s.StopWatching(); s.Dispose(); }
            var src = CreateSource(root);
            _sources[root] = src;
            _ = BuildRootAsync(src, _lifetime.Token);
        }
    }

    public void Pause(bool paused)
    {
        _paused = paused;
        foreach (var s in _sources.Values)
        {
            if (paused) s.StopWatching();
            else
            {
                if (s is UsnIndexSource usn && usn.Target == null) continue;
                s.StartWatching(_changes.Writer);
            }
        }
        UpdateReadyStatus();
    }

    public void RequestSave() => _saveRequested = true;

    public IReadOnlyList<VolumeStats> GetStats() =>
        _index.VolumesArray.Select(v => new VolumeStats(v.RootPath, v.Kind, v.LiveCount, v.LastUpdatedUtc, v.LastBuildTime)).ToList();

    private void SaveCore()
    {
        try
        {
            var vols = _index.VolumesArray;
            if (vols.Length == 0) return;
            if (!vols.Any(v => v.Dirty) && File.Exists(AppPaths.IndexFile)) return;
            var sw = Stopwatch.StartNew();
            // Compaction runs on this (sole writer) thread under a read lock, then swaps in the compacted copy.
            foreach (var v in vols)
            {
                if (!v.ShouldCompact) continue;
                VolumeIndex compacted;
                v.Lock.EnterReadLock();
                try { compacted = v.Compact(); } finally { v.Lock.ExitReadLock(); }
                if (_sources.TryGetValue(v.RootPath, out var src) && src is UsnIndexSource usn) usn.Target = compacted;
                _index.SetVolume(compacted);
            }
            IndexPersistence.Save(AppPaths.IndexFile, _matcher.Fingerprint, _index.VolumesArray);
            Logger.Info($"Index saved in {sw.ElapsedMilliseconds}ms");
        }
        catch (Exception ex) { Logger.Error("Index save failed", ex); }
    }

    /// <summary>Shutdown: stop the writer thread and watchers, then save synchronously on the calling thread.</summary>
    public void ShutdownAndSave()
    {
        _lifetime.Cancel();
        foreach (var b in _builds.Values) { try { b.Cancel(); } catch { } }
        foreach (var s in _sources.Values) { try { s.StopWatching(); } catch { } }
        _writer?.Join(3000);
        SaveCore();
    }

    private void OnPinyinUpdated()
    {
        foreach (var v in _index.VolumesArray)
        {
            v.Lock.EnterWriteLock();
            try { v.BuildPinyin(); } finally { v.Lock.ExitWriteLock(); }
        }
    }

    public void Dispose()
    {
        PinyinInitialsTable.Updated -= OnPinyinUpdated;
        _lifetime.Cancel();
        foreach (var s in _sources.Values) { try { s.Dispose(); } catch { } }
        foreach (var t in _rebuildTimers.Values) t.Dispose();
        _saveTimer?.Dispose();
        _exclusionTimer?.Dispose();
        _writer?.Join(2000);
    }
}

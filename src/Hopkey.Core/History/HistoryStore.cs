using System.Text.Json;
using Hopkey.Core.Config;
using Hopkey.Core.Util;

namespace Hopkey.Core.History;

/// <summary>Usage records, recent folders and query associations. Thread-safe; debounced atomic save.</summary>
public sealed class HistoryStore : IDisposable
{
    private const int MaxRecentFolders = 100;
    private const int MaxUsage = 2000;
    private const int MaxQueriesPerItem = 10;

    private readonly string _path;
    private readonly object _gate = new();
    private readonly Dictionary<string, UsageRecord> _usage = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<RecentFolder> _recent = new();
    private readonly Dictionary<string, int> _facetUsage = new(StringComparer.OrdinalIgnoreCase);
    private readonly Timer _saveTimer;
    private bool _dirty;

    public event Action? Changed;

    private HistoryStore(string path)
    {
        _path = path;
        _saveTimer = new Timer(_ => SaveNow(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public static HistoryStore Load(string? path = null)
    {
        var store = new HistoryStore(path ?? AppPaths.HistoryFile);
        try
        {
            if (File.Exists(store._path))
            {
                ReadOnlySpan<byte> bytes = File.ReadAllBytes(store._path);
                if (bytes.StartsWith("﻿"u8)) bytes = bytes[3..];
                var data = JsonSerializer.Deserialize(bytes, JsonContext.Default.HistoryData);
                if (data != null)
                {
                    foreach (var u in data.Usage ?? new())
                        if (!string.IsNullOrEmpty(u.Key)) { u.Queries ??= new(); store._usage[u.Key] = u; }
                    store._recent.AddRange((data.RecentFolders ?? new()).Where(r => !string.IsNullOrEmpty(r.Path)));
                    foreach (var (token, n) in data.FacetUsage ?? new())
                        if (!string.IsNullOrEmpty(token) && n > 0) store._facetUsage[token] = n;
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Error("history.json is corrupt; starting fresh", ex);
            try { File.Copy(store._path, store._path + ".bad", true); } catch { }
        }
        return store;
    }

    public static string NormalizeQuery(string? q) => (q ?? string.Empty).Trim().ToLowerInvariant();

    public void RecordLaunch(string key, UsageKind kind, string? query)
    {
        if (string.IsNullOrEmpty(key)) return;
        lock (_gate)
        {
            if (!_usage.TryGetValue(key, out var rec))
            {
                rec = new UsageRecord { Key = key, Kind = kind };
                _usage[key] = rec;
                if (_usage.Count > MaxUsage) TrimUsage();
            }
            rec.Count++;
            rec.LastUsedUtc = DateTime.UtcNow;
            var nq = NormalizeQuery(query);
            if (nq.Length > 0)
            {
                rec.Queries.TryGetValue(nq, out int c);
                rec.Queries[nq] = c + 1;
                if (rec.Queries.Count > MaxQueriesPerItem)
                {
                    var min = rec.Queries.MinBy(kv => kv.Value).Key;
                    rec.Queries.Remove(min);
                }
            }
        }
        ScheduleSave();
    }

    private void TrimUsage()
    {
        // Drop the 10% least valuable records.
        var victims = _usage.Values
            .OrderBy(u => Math.Log(1 + u.Count) + Math.Exp(-(DateTime.UtcNow - u.LastUsedUtc).TotalDays / 30))
            .Take(MaxUsage / 10).Select(u => u.Key).ToList();
        foreach (var v in victims) _usage.Remove(v);
    }

    public void RecordFolderVisit(string folder, FolderVisitSource source)
    {
        if (!PathUtil.IsFileSystemPath(folder)) return;
        folder = PathUtil.TrimEndSeparator(folder);
        lock (_gate)
        {
            int idx = _recent.FindIndex(r => PathUtil.Equal(r.Path, folder));
            if (idx == 0) { _recent[0].LastVisitUtc = DateTime.UtcNow; }
            else
            {
                if (idx > 0) _recent.RemoveAt(idx);
                _recent.Insert(0, new RecentFolder { Path = folder, LastVisitUtc = DateTime.UtcNow, Source = source });
                if (_recent.Count > MaxRecentFolders) _recent.RemoveRange(MaxRecentFolders, _recent.Count - MaxRecentFolders);
            }
        }
        ScheduleSave();
    }

    /// <summary>Boost = 120·ln(1+count) + 150·exp(−ageDays/7) + 300·(query association).</summary>
    public double GetBoost(string key, string normalizedQuery)
    {
        lock (_gate)
        {
            if (!_usage.TryGetValue(key, out var rec)) return 0;
            double ageDays = (DateTime.UtcNow - rec.LastUsedUtc).TotalDays;
            double boost = 120 * Math.Log(1 + rec.Count) + 150 * Math.Exp(-ageDays / 7);
            if (normalizedQuery.Length > 0)
            {
                foreach (var q in rec.Queries.Keys)
                {
                    if (q.StartsWith(normalizedQuery, StringComparison.Ordinal) || normalizedQuery.StartsWith(q, StringComparison.Ordinal))
                    {
                        boost += 300;
                        break;
                    }
                }
            }
            return boost;
        }
    }

    /// <summary>Count a click on a launcher type-filter chip; more clicks rank that chip earlier.</summary>
    public void RecordFacet(string token)
    {
        if (string.IsNullOrEmpty(token)) return;
        lock (_gate)
        {
            _facetUsage[token] = _facetUsage.GetValueOrDefault(token) + 1;
            if (_facetUsage.Count > 200)
                foreach (var k in _facetUsage.OrderBy(kv => kv.Value).Take(50).Select(kv => kv.Key).ToList()) _facetUsage.Remove(k);
        }
        ScheduleSave();
    }

    public int GetFacetCount(string token)
    {
        lock (_gate) return _facetUsage.GetValueOrDefault(token);
    }

    public bool HasUsage(string key)
    {
        lock (_gate) return _usage.ContainsKey(key);
    }

    public IReadOnlyList<RecentFolder> GetRecentFolders(int max, bool onlyExisting = true)
    {
        List<RecentFolder> snapshot;
        lock (_gate) snapshot = _recent.Take(Math.Max(max * 2, max + 10)).ToList();
        if (!onlyExisting) return snapshot.Take(max).ToList();
        var result = new List<RecentFolder>(max);
        var deadline = Environment.TickCount64 + 200;
        foreach (var r in snapshot)
        {
            if (result.Count >= max) break;
            if (Environment.TickCount64 > deadline || PathUtil.IsNetworkPath(r.Path) || Directory.Exists(r.Path))
                result.Add(r);
        }
        return result;
    }

    public IReadOnlyList<UsageRecord> GetTop(int max)
    {
        lock (_gate)
        {
            var now = DateTime.UtcNow;
            return _usage.Values
                .OrderByDescending(u => 120 * Math.Log(1 + u.Count) + 150 * Math.Exp(-(now - u.LastUsedUtc).TotalDays / 7))
                .Take(max).ToList();
        }
    }

    public IReadOnlyList<UsageRecord> GetRecentUsage(int max)
    {
        lock (_gate) return _usage.Values.OrderByDescending(u => u.LastUsedUtc).Take(max).ToList();
    }

    /// <summary>All usage records (snapshot) for history search.</summary>
    public IReadOnlyList<UsageRecord> Snapshot()
    {
        lock (_gate) return _usage.Values.ToList();
    }

    public void Remove(string key)
    {
        lock (_gate)
        {
            _usage.Remove(key);
            _recent.RemoveAll(r => PathUtil.Equal(r.Path, key));
        }
        ScheduleSave();
    }

    public void ClearRecent()
    {
        lock (_gate) _recent.Clear();
        ScheduleSave();
    }

    public void ClearAll()
    {
        lock (_gate) { _recent.Clear(); _usage.Clear(); _facetUsage.Clear(); }
        ScheduleSave();
    }

    public void ScheduleSave()
    {
        _dirty = true;
        _saveTimer.Change(2000, Timeout.Infinite);
        try { Changed?.Invoke(); } catch { }
    }

    public void SaveNow()
    {
        if (!_dirty) return;
        try
        {
            HistoryData data;
            lock (_gate)
            {
                data = new HistoryData
                {
                    Usage = _usage.Values.Select(u => new UsageRecord
                    {
                        Key = u.Key, Kind = u.Kind, Count = u.Count, LastUsedUtc = u.LastUsedUtc, Queries = new(u.Queries),
                    }).ToList(),
                    RecentFolders = _recent.Select(r => new RecentFolder { Path = r.Path, LastVisitUtc = r.LastVisitUtc, Source = r.Source }).ToList(),
                    FacetUsage = new(_facetUsage),
                };
                _dirty = false;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            AppPaths.AtomicWrite(_path, JsonSerializer.SerializeToUtf8Bytes(data, JsonContext.Default.HistoryData));
        }
        catch (Exception ex) { Logger.Error("Failed to save history", ex); }
    }

    public void Dispose()
    {
        _saveTimer.Dispose();
        SaveNow();
    }
}

using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Channels;
using Hopkey.Core.Exclusion;
using Hopkey.Core.Native;
using Hopkey.Core.Util;
using Microsoft.Win32.SafeHandles;

namespace Hopkey.Core.Indexing;

/// <summary>
/// NTFS index source (Everything-style): FSCTL_ENUM_USN_DATA for the initial build, USN journal for catch-up and live updates.
/// Requires administrator rights. Volume root only.
/// </summary>
public sealed unsafe class UsnIndexSource : IIndexSource
{
    private const int BufferSize = 1 << 20;
    private const uint WatchReasons = UsnNative.USN_REASON_FILE_CREATE | UsnNative.USN_REASON_FILE_DELETE |
                                      UsnNative.USN_REASON_RENAME_NEW_NAME | UsnNative.USN_REASON_BASIC_INFO_CHANGE |
                                      UsnNative.USN_REASON_HARD_LINK_CHANGE;

    private readonly string _root;      // "C:\"
    private readonly string _volumePath; // "\\.\C:"
    private Thread? _watchThread;
    private CancellationTokenSource? _watchCts;
    private ulong _rootFrn;

    /// <summary>Tests only: force "mft", "layout" or "enum" as the build enumeration (default: mft → layout → enum).</summary>
    internal static string? TestMethod;
    /// <summary>The enumeration the last build used.</summary>
    internal string? LastMethod { get; private set; }
    /// <summary>Why the preferred enumeration was not used (diagnostics).</summary>
    internal string? LastFallbackReason { get; private set; }

    public string RootPath => _root;
    public IndexSourceKind Kind => IndexSourceKind.Usn;
    public event Action<string>? RebuildRequired;

    /// <summary>Last USN position read by the watcher (persisted via VolumeIndex.NextUsn).</summary>
    public VolumeIndex? Target { get; set; }

    public UsnIndexSource(string root)
    {
        _root = PathUtil.EnsureEndSeparator(root.Substring(0, 2));
        _volumePath = @"\\.\" + root.Substring(0, 2);
    }

    /// <summary>Can this root be indexed via USN? (NTFS + elevated + journal active)</summary>
    public static bool IsSupported(string root)
    {
        if (!ElevationHelper.IsSelfElevated) return false;
        if (root.Length > 3 || root.Length < 2 || root[1] != ':') return false;
        try
        {
            var fs = new StringBuilder(32);
            if (!Kernel32.GetVolumeInformation(PathUtil.EnsureEndSeparator(root.Substring(0, 2)), null, 0, out _, out _, out _, fs, fs.Capacity)) return false;
            if (!fs.ToString().Equals("NTFS", StringComparison.OrdinalIgnoreCase)) return false;
            using var h = OpenVolume(@"\\.\" + root.Substring(0, 2));
            if (h.IsInvalid) return false;
            return QueryJournal(h, out _);
        }
        catch { return false; }
    }

    public static uint GetVolumeSerial(string root)
    {
        var fs = new StringBuilder(32);
        return Kernel32.GetVolumeInformation(PathUtil.EnsureEndSeparator(root.Substring(0, 2)), null, 0, out uint serial, out _, out _, fs, fs.Capacity) ? serial : 0;
    }

    private static SafeFileHandle OpenVolume(string volumePath) =>
        Kernel32.CreateFile(volumePath, Kernel32.GENERIC_READ, Kernel32.FILE_SHARE_READ | Kernel32.FILE_SHARE_WRITE, IntPtr.Zero, Kernel32.OPEN_EXISTING, 0, IntPtr.Zero);

    private static bool QueryJournal(SafeFileHandle h, out USN_JOURNAL_DATA_V0 data)
    {
        USN_JOURNAL_DATA_V0 d;
        bool ok = Kernel32.DeviceIoControl(h, UsnNative.FSCTL_QUERY_USN_JOURNAL, null, 0, &d, sizeof(USN_JOURNAL_DATA_V0), out _, IntPtr.Zero);
        data = d;
        return ok;
    }

    private ulong GetRootFrn()
    {
        using var h = Kernel32.CreateFile(_root, 0, Kernel32.FILE_SHARE_READ | Kernel32.FILE_SHARE_WRITE | Kernel32.FILE_SHARE_DELETE,
            IntPtr.Zero, Kernel32.OPEN_EXISTING, Kernel32.FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
        if (h.IsInvalid) throw new IOException($"Cannot open root {_root}", Marshal.GetLastWin32Error());
        if (!Kernel32.GetFileInformationByHandle(h, out var info)) throw new IOException("GetFileInformationByHandle failed", Marshal.GetLastWin32Error());
        return ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow;
    }

    public Task<VolumeIndex> BuildAsync(ExclusionMatcher matcher, IProgress<long>? progress, CancellationToken ct) =>
        Task.Factory.StartNew(() => Build(matcher, progress, ct), ct, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    private VolumeIndex Build(ExclusionMatcher matcher, IProgress<long>? progress, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        _rootFrn = GetRootFrn();
        using var h = OpenVolume(_volumePath);
        if (h.IsInvalid) throw new IOException($"Cannot open volume {_volumePath}", Marshal.GetLastWin32Error());
        if (!QueryJournal(h, out var journal)) throw new IOException("USN journal not active", Marshal.GetLastWin32Error());

        // Every method but the last returns all hard-link names. Fastest first (measured: C: 903k names in 1.1-1.4 s,
        // E: 324k in 0.33 s): FSCTL_GET_NTFS_FILE_RECORD per record in parallel → FSCTL_QUERY_FILE_LAYOUT (refused on
        // the system volume) → raw $MFT read (refused where raw volume reads are blocked) → FSCTL_ENUM_USN_DATA
        // (one name per file).
        string[] order = TestMethod is { } forced ? [forced] : ["record", "layout", "mft"];
        RawRecords raw = null!;
        string method = "enum";
        var reasons = new List<string>();
        foreach (var m in order)
        {
            ct.ThrowIfCancellationRequested();
            raw = new RawRecords(1 << 18);
            string? why = null;
            bool ok = m switch
            {
                "layout" => EnumerateLayout(h, raw, progress, ct, out why),
                "mft" => MftReader.TryEnumerate(h, _rootFrn, raw, progress, ct, out why),
                "record" => MftReader.TryEnumerateByRecord(_volumePath, _rootFrn, raw, progress, ct, out why),
                _ => false,
            };
            if (ok) { method = m; break; }
            if (m != "enum") reasons.Add($"{m}: {why}");
        }
        LastFallbackReason = reasons.Count > 0 ? string.Join("; ", reasons) : null;
        if (LastFallbackReason != null) Logger.Info($"USN build {_root}: {LastFallbackReason}");
        if (method == "enum")
        {
            raw = new RawRecords(1 << 18);
            EnumerateUsnData(h, journal, raw, progress, ct);
        }

        var enumTime = sw.Elapsed;
        LastMethod = method;
        var v = IndexBuilder.BuildFromRaw(_root, _rootFrn, raw, matcher, ct);
        v.JournalId = journal.UsnJournalID;
        v.NextUsn = journal.NextUsn;
        v.VolumeSerial = GetVolumeSerial(_root);
        v.LastBuildTime = sw.Elapsed;
        v.LastUpdatedUtc = DateTime.UtcNow;
        Logger.Info($"USN build {_root} ({method}): {raw.Count:N0} names, {v.LiveCount:N0} kept, enum {enumTime.TotalMilliseconds:F0}ms, total {sw.ElapsedMilliseconds}ms");
        progress?.Report(v.LiveCount);
        return v;
    }

    /// <summary>
    /// All names of all files via FSCTL_QUERY_FILE_LAYOUT (Windows 8+): unlike FSCTL_ENUM_USN_DATA it returns every
    /// hard link. Extra links get <see cref="LinkKey"/> keys. Returns false if the control code isn't supported.
    /// </summary>
    private bool EnumerateLayout(SafeFileHandle h, RawRecords raw, IProgress<long>? progress, CancellationToken ct, out string? failure)
    {
        failure = null;
        byte* input = stackalloc byte[UsnNative.QueryFileLayoutInputSize];
        new Span<byte>(input, UsnNative.QueryFileLayoutInputSize).Clear();
        *(uint*)(input + 4) = UsnNative.QUERY_FILE_LAYOUT_RESTART | UsnNative.QUERY_FILE_LAYOUT_INCLUDE_NAMES;
        byte* buffer = (byte*)NativeMemory.Alloc(BufferSize);
        bool first = true;
        int lastReport = 0;
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (!Kernel32.DeviceIoControl(h, UsnNative.FSCTL_QUERY_FILE_LAYOUT, input, UsnNative.QueryFileLayoutInputSize, buffer, BufferSize, out int bytes, IntPtr.Zero))
                {
                    int err = Marshal.GetLastWin32Error();
                    if (err == Kernel32.ERROR_HANDLE_EOF) return true;
                    failure = $"FSCTL_QUERY_FILE_LAYOUT error {err}";
                    if (first) return false;
                    throw new IOException(failure, err);
                }
                first = false;
                *(uint*)(input + 4) = UsnNative.QUERY_FILE_LAYOUT_INCLUDE_NAMES; // continue the enumeration
                uint count = *(uint*)buffer;
                if (count == 0) return true;
                byte* e = buffer + *(uint*)(buffer + 4);
                byte* end = buffer + bytes;
                for (uint n = 0; n < count && e < end; n++)
                {
                    ulong frn = *(ulong*)(e + UsnNative.FLE_Frn);
                    uint nameOff = *(uint*)(e + UsnNative.FLE_FirstNameOffset);
                    if ((frn & 0x0000FFFFFFFFFFFF) >= 24 && frn != _rootFrn && nameOff != 0)
                        AddLayoutNames(raw, frn, *(uint*)(e + UsnNative.FLE_Attributes), e + nameOff);
                    uint next = *(uint*)(e + UsnNative.FLE_NextFileOffset);
                    if (next == 0) break;
                    e += next;
                }
                if (raw.Count - lastReport > 65536) { lastReport = raw.Count; progress?.Report(raw.Count); }
            }
        }
        finally { NativeMemory.Free(buffer); }
    }

    private static void AddLayoutNames(RawRecords raw, ulong frn, uint attrs, byte* name)
    {
        int ordinal = 0;
        bool canLink = LinkKey.CanEncode(frn) && (attrs & UsnNative.FILE_ATTRIBUTE_DIRECTORY) == 0;
        while (true)
        {
            uint flags = *(uint*)(name + UsnNative.FLN_Flags);
            // A pure 8.3 (DOS-namespace) name duplicates the long name of the same link: skip it.
            if ((flags & (UsnNative.FILE_LAYOUT_NAME_ENTRY_DOS | UsnNative.FILE_LAYOUT_NAME_ENTRY_PRIMARY)) != UsnNative.FILE_LAYOUT_NAME_ENTRY_DOS)
            {
                ulong parent = *(ulong*)(name + UsnNative.FLN_Parent);
                int len = (int)(*(uint*)(name + UsnNative.FLN_NameLength) / 2);
                var span = new ReadOnlySpan<char>(name + UsnNative.FLN_Name, len);
                if (ordinal == 0) raw.Add(frn, parent, attrs, span);
                else if (canLink && ordinal <= LinkKey.MaxExtra) raw.Add(LinkKey.Extra(frn, ordinal), parent, attrs, span);
                ordinal++;
            }
            uint next = *(uint*)(name + UsnNative.FLN_Next);
            if (next == 0) return;
            name += next;
        }
    }

    private void EnumerateUsnData(SafeFileHandle h, USN_JOURNAL_DATA_V0 journal, RawRecords raw, IProgress<long>? progress, CancellationToken ct)
    {
        byte* buffer = (byte*)NativeMemory.Alloc(BufferSize);
        try
        {
            var med = new MFT_ENUM_DATA_V0 { StartFileReferenceNumber = 0, LowUsn = 0, HighUsn = journal.NextUsn };
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (!Kernel32.DeviceIoControl(h, UsnNative.FSCTL_ENUM_USN_DATA, &med, sizeof(MFT_ENUM_DATA_V0), buffer, BufferSize, out int bytes, IntPtr.Zero))
                {
                    int err = Marshal.GetLastWin32Error();
                    if (err == Kernel32.ERROR_HANDLE_EOF) break;
                    throw new IOException($"FSCTL_ENUM_USN_DATA failed ({err})", err);
                }
                if (bytes <= 8) break;
                med.StartFileReferenceNumber = *(ulong*)buffer;
                int pos = 8;
                while (pos < bytes)
                {
                    byte* rec = buffer + pos;
                    uint len = *(uint*)rec;
                    if (len == 0) break;
                    ushort major = *(ushort*)(rec + UsnNative.MajorVersionOffset);
                    if (major == 2)
                    {
                        ulong frn = *(ulong*)(rec + UsnNative.V2_FrnOffset);
                        // Skip NTFS metafiles ($MFT…$Extend, reserved records < 24) and the root itself
                        if ((frn & 0x0000FFFFFFFFFFFF) >= 24 && frn != _rootFrn)
                        {
                            ulong parent = *(ulong*)(rec + UsnNative.V2_ParentFrnOffset);
                            uint attrs = *(uint*)(rec + UsnNative.V2_AttributesOffset);
                            int nameLen = *(ushort*)(rec + UsnNative.V2_FileNameLengthOffset) / 2;
                            int nameOff = *(ushort*)(rec + UsnNative.V2_FileNameOffsetOffset);
                            raw.Add(frn, parent, attrs, new ReadOnlySpan<char>(rec + nameOff, nameLen));
                        }
                    }
                    pos += (int)len;
                }
                progress?.Report(raw.Count);
            }
        }
        finally { NativeMemory.Free(buffer); }
    }

    public Task<bool> CatchUpAsync(VolumeIndex cached, ExclusionMatcher matcher, CancellationToken ct) =>
        Task.Factory.StartNew(() =>
        {
            try
            {
                _rootFrn = cached.Frn != null && cached.Count > 0 ? cached.Frn[0] : GetRootFrn();
                using var h = OpenVolume(_volumePath);
                if (h.IsInvalid || !QueryJournal(h, out var journal)) return false;
                if (journal.UsnJournalID != cached.JournalId || cached.NextUsn < journal.LowestValidUsn || cached.NextUsn > journal.NextUsn)
                {
                    Logger.Info($"USN journal for {_root} changed or wrapped; full rebuild required");
                    return false;
                }
                var changes = new List<IndexChange>(4096);
                long next = ReadJournal(h, cached.JournalId, cached.NextUsn, changes, ct, out bool broken);
                if (broken) return false;
                cached.Lock.EnterWriteLock();
                try
                {
                    UsnChangeApplier.Apply(cached, changes, matcher);
                    cached.NextUsn = next;
                }
                finally { cached.Lock.ExitWriteLock(); }
                Logger.Info($"USN catch-up {_root}: {changes.Count} changes applied");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Warn($"USN catch-up failed for {_root}", ex);
                return false;
            }
        }, ct, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    /// <summary>Reads journal records from <paramref name="startUsn"/> until no more data. Returns next USN.</summary>
    private long ReadJournal(SafeFileHandle h, ulong journalId, long startUsn, List<IndexChange> output, CancellationToken ct, out bool broken)
    {
        broken = false;
        byte* buffer = (byte*)NativeMemory.Alloc(BufferSize);
        try
        {
            var rd = new READ_USN_JOURNAL_DATA_V0
            {
                StartUsn = startUsn, ReasonMask = WatchReasons, ReturnOnlyOnClose = 0, Timeout = 0, BytesToWaitFor = 0, UsnJournalID = journalId,
            };
            while (!ct.IsCancellationRequested)
            {
                if (!Kernel32.DeviceIoControl(h, UsnNative.FSCTL_READ_USN_JOURNAL, &rd, sizeof(READ_USN_JOURNAL_DATA_V0), buffer, BufferSize, out int bytes, IntPtr.Zero))
                {
                    int err = Marshal.GetLastWin32Error();
                    if (err is Kernel32.ERROR_JOURNAL_ENTRY_DELETED or Kernel32.ERROR_JOURNAL_NOT_ACTIVE or Kernel32.ERROR_JOURNAL_DELETE_IN_PROGRESS)
                    {
                        broken = true;
                        return rd.StartUsn;
                    }
                    throw new IOException($"FSCTL_READ_USN_JOURNAL failed ({err})", err);
                }
                long nextUsn = *(long*)buffer;
                int pos = 8;
                while (pos < bytes)
                {
                    byte* rec = buffer + pos;
                    uint len = *(uint*)rec;
                    if (len == 0) break;
                    if (*(ushort*)(rec + UsnNative.MajorVersionOffset) == 2)
                        ParseRecordV2(rec, output);
                    pos += (int)len;
                }
                if (nextUsn == rd.StartUsn || bytes <= 8) { rd.StartUsn = nextUsn; break; }
                rd.StartUsn = nextUsn;
            }
            return rd.StartUsn;
        }
        finally { NativeMemory.Free(buffer); }
    }

    private void ParseRecordV2(byte* rec, List<IndexChange> output)
    {
        ulong frn = *(ulong*)(rec + UsnNative.V2_FrnOffset);
        if ((frn & 0x0000FFFFFFFFFFFF) < 24) return;
        ulong parent = *(ulong*)(rec + UsnNative.V2_ParentFrnOffset);
        uint reason = *(uint*)(rec + UsnNative.V2_ReasonOffset);
        uint attrs = *(uint*)(rec + UsnNative.V2_AttributesOffset);
        int nameLen = *(ushort*)(rec + UsnNative.V2_FileNameLengthOffset) / 2;
        int nameOff = *(ushort*)(rec + UsnNative.V2_FileNameOffsetOffset);
        string name = new((char*)(rec + nameOff), 0, nameLen);
        ChangeKind kind;
        if ((reason & UsnNative.USN_REASON_FILE_DELETE) != 0) kind = ChangeKind.Deleted;
        // A link was added or removed (the record names that link; FILE_DELETE only comes with the last one).
        else if ((reason & UsnNative.USN_REASON_HARD_LINK_CHANGE) != 0 && (attrs & UsnNative.FILE_ATTRIBUTE_DIRECTORY) == 0) kind = ChangeKind.HardLinkChanged;
        else if ((reason & UsnNative.USN_REASON_RENAME_NEW_NAME) != 0) kind = ChangeKind.Renamed;
        else if ((reason & UsnNative.USN_REASON_FILE_CREATE) != 0) kind = ChangeKind.Created;
        else if ((reason & UsnNative.USN_REASON_BASIC_INFO_CHANGE) != 0) kind = ChangeKind.AttributesChanged;
        else return;
        output.Add(new IndexChange(kind, frn, parent, name, (FileAttributes)attrs, null, null));
    }

    public void StartWatching(ChannelWriter<(string Root, IndexChange Change)> sink)
    {
        StopWatching();
        var cts = new CancellationTokenSource();
        _watchCts = cts;
        _watchThread = new Thread(() => WatchLoop(sink, cts.Token)) { IsBackground = true, Name = "CL-USN " + _root, Priority = ThreadPriority.BelowNormal };
        _watchThread.Start();
    }

    private void WatchLoop(ChannelWriter<(string Root, IndexChange Change)> sink, CancellationToken ct)
    {
        try
        {
            using var h = OpenVolume(_volumePath);
            if (h.IsInvalid) { RebuildRequired?.Invoke(_root); return; }
            var target = Target;
            if (target == null) return;
            ulong journalId = target.JournalId;
            long usn = target.NextUsn;
            var batch = new List<IndexChange>(256);
            while (!ct.IsCancellationRequested)
            {
                batch.Clear();
                long next = ReadJournal(h, journalId, usn, batch, ct, out bool broken);
                if (broken)
                {
                    Logger.Warn($"USN journal broken on {_root}; requesting rebuild");
                    RebuildRequired?.Invoke(_root);
                    return;
                }
                foreach (var c in batch) sink.TryWrite((_root, c));
                if (next != usn)
                {
                    usn = next;
                    // Position is applied by the writer thread together with the batch (NextUsn in VolumeIndex).
                    sink.TryWrite((_root, new IndexChange((ChangeKind)255, (ulong)next, 0, null, 0, null, null)));
                }
                ct.WaitHandle.WaitOne(500);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Logger.Error($"USN watcher failed for {_root}", ex);
            RebuildRequired?.Invoke(_root);
        }
    }

    public void StopWatching()
    {
        _watchCts?.Cancel();
        _watchThread?.Join(2000);
        _watchCts?.Dispose();
        _watchCts = null;
        _watchThread = null;
    }

    public void Dispose() => StopWatching();
}

/// <summary>Applies USN (key-based) changes to a volume index. Caller holds the write lock.</summary>
internal static class UsnChangeApplier
{
    /// <summary>Marker kind used to carry the journal position through the change channel.</summary>
    public const ChangeKind PositionMarker = (ChangeKind)255;

    public static void Apply(VolumeIndex v, IReadOnlyList<IndexChange> changes, ExclusionMatcher m)
    {
        foreach (var c in changes)
        {
            if (c.Kind == PositionMarker) { v.NextUsn = (long)c.Key; continue; }
            try { ApplyOne(v, c, m); }
            catch (Exception ex) { Logger.Warn($"Failed to apply USN change {c.Kind} {c.Name}", ex); }
        }
        v.LastUpdatedUtc = DateTime.UtcNow;
    }

    private static void ApplyOne(VolumeIndex v, IndexChange c, ExclusionMatcher m)
    {
        bool exists = v.TryFindByKey(c.Key, out int idx);
        bool linked = v.LinkedBases != null && v.LinkedBases.ContainsKey(c.Key);
        switch (c.Kind)
        {
            case ChangeKind.Deleted:
                // NTFS emits a delete record for every child before the directory itself, so O(1) here.
                if (exists) v.MarkDeleted(idx);
                if (linked)
                {
                    // The last name went away: so did every other hard link of this file.
                    var fam = new List<int>(4);
                    v.GetLinkFamily(c.Key, fam);
                    foreach (int i in fam) v.MarkDeleted(i);
                    v.LinkedBases!.Remove(c.Key);
                }
                return;
            case ChangeKind.HardLinkChanged:
            case ChangeKind.Created or ChangeKind.Renamed when linked && (c.Attributes & FileAttributes.Directory) == 0:
                ReconcileLinks(v, c, m);
                return;
            case ChangeKind.Created:
            case ChangeKind.Renamed:
            {
                if (!v.TryFindByKey(c.ParentKey, out int parent))
                {
                    // Parent not indexed (excluded/unknown) → item should not be indexed.
                    if (exists) v.DeleteSubtree(idx);
                    return;
                }
                bool isDir = (c.Attributes & FileAttributes.Directory) != 0;
                var parentPath = v.GetFullPath(parent);
                if (m.IsExcludedEntry(c.Name, parentPath, isDir, c.Attributes))
                {
                    if (exists) v.DeleteSubtree(idx);
                    return;
                }
                var flags = FlagsUtil.FromAttributes(c.Attributes);
                if (exists)
                {
                    if (v.Parent[idx] != parent || !v.GetName(idx).SequenceEqual(c.Name))
                        v.RenameOrMove(idx, parent, c.Name, flags);
                }
                else
                {
                    v.AppendRaw(parent, c.Name, flags, c.Key);
                    // A directory moved in from an excluded/unindexed area brings children we don't have.
                    if (isDir && c.Kind == ChangeKind.Renamed) v.NeedsRebuild = true;
                }
                return;
            }
            case ChangeKind.AttributesChanged:
            {
                var flags = FlagsUtil.FromAttributes(c.Attributes);
                List<int> targets;
                if (linked) { targets = new List<int>(4); v.GetLinkFamily(c.Key, targets); } // attributes are per file, shared by all links
                else if (exists) targets = [idx];
                else return;
                foreach (int i in targets)
                {
                    v.SetAttributes(i, flags);
                    int parent = v.Parent[i];
                    var parentPath = parent >= 0 ? v.GetFullPath(parent) : null;
                    if (m.IsExcludedEntry(v.GetName(i), parentPath, v.IsDirectory(i), c.Attributes))
                        v.DeleteSubtree(i);
                }
                return;
            }
        }
    }

    /// <summary>
    /// A name of a hard-linked file appeared, disappeared or moved. The journal record only carries the name involved,
    /// not whether it was added or removed, so the disk decides: index the name if it exists, drop the family's
    /// names that no longer exist (re-using one of those slots when this record is the new name of a rename).
    /// </summary>
    private static void ReconcileLinks(VolumeIndex v, IndexChange c, ExclusionMatcher m)
    {
        var fam = new List<int>(4);
        v.GetLinkFamily(c.Key, fam);
        int parent = -1;
        string? path = null;
        bool onDisk = false, excluded = true;
        if (c.Name != null && v.TryFindByKey(c.ParentKey, out parent))
        {
            var parentPath = v.GetFullPath(parent);
            path = Path.Join(parentPath, c.Name);
            onDisk = File.Exists(path);
            excluded = m.IsExcludedEntry(c.Name, parentPath, false, c.Attributes);
        }
        var flags = FlagsUtil.FromAttributes(c.Attributes);
        int match = -1, reuse = -1;
        foreach (int i in fam)
        {
            if (path != null && v.Parent[i] == parent && v.GetName(i).Equals(c.Name, StringComparison.OrdinalIgnoreCase)) { match = i; continue; }
            if (File.Exists(v.GetFullPath(i))) continue;
            // This name is gone. Re-use the first such slot for the new name of a rename, drop the rest.
            if (reuse < 0 && onDisk && !excluded) reuse = i;
            else v.MarkDeleted(i);
        }
        if (match >= 0)
        {
            if (!onDisk || excluded) v.MarkDeleted(match);
            else if (!v.GetName(match).SequenceEqual(c.Name)) v.RenameOrMove(match, parent, c.Name, flags);
            else v.SetAttributes(match, flags);
            if (reuse >= 0) v.MarkDeleted(reuse);
            return;
        }
        if (!onDisk || excluded) return;
        if (reuse >= 0) v.RenameOrMove(reuse, parent, c.Name, flags);
        else v.AddLink(c.Key, parent, c.Name, flags);
    }
}

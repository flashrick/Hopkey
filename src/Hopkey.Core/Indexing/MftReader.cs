using System.Runtime.InteropServices;
using Hopkey.Core.Native;
using Hopkey.Core.Util;
using Microsoft.Win32.SafeHandles;

namespace Hopkey.Core.Indexing;

/// <summary>
/// Reads the NTFS master file table straight from the volume (like Everything): $MFT's data runs from record 0, then
/// one sequential pass over all file records, collecting every $FILE_NAME — i.e. every hard link, which
/// FSCTL_ENUM_USN_DATA can't return and FSCTL_QUERY_FILE_LAYOUT refuses on the system volume.
/// Any inconsistency makes it give up so the caller can fall back to the FSCTL enumerations.
/// </summary>
internal static unsafe class MftReader
{
    private const uint FileSignature = 0x454C4946; // "FILE"
    private const uint AttrStandardInformation = 0x10;
    private const uint AttrFileName = 0x30;
    private const uint AttrData = 0x80;
    private const uint AttrEnd = 0xFFFFFFFF;
    private const ushort RecordInUse = 0x1, RecordIsDirectory = 0x2;
    private const int FixupStride = 512;
    private const int ChunkSize = 4 << 20;

    private readonly record struct Run(long Lcn, long Clusters);

    public static bool TryEnumerate(SafeFileHandle volume, ulong rootFrn, RawRecords raw, IProgress<long>? progress, CancellationToken ct, out string? failure)
    {
        failure = null;
        NTFS_VOLUME_DATA_BUFFER vd;
        if (!Kernel32.DeviceIoControl(volume, UsnNative.FSCTL_GET_NTFS_VOLUME_DATA, null, 0, &vd, sizeof(NTFS_VOLUME_DATA_BUFFER), out _, IntPtr.Zero))
        {
            failure = $"FSCTL_GET_NTFS_VOLUME_DATA failed ({Marshal.GetLastWin32Error()})";
            return false;
        }
        int recSize = (int)vd.BytesPerFileRecordSegment;
        int cluster = (int)vd.BytesPerCluster;
        int sector = (int)Math.Max(512, vd.BytesPerSector);
        if (recSize < 1024 || recSize > 4096 || cluster < sector || ChunkSize % cluster != 0 || recSize % FixupStride != 0)
        {
            failure = $"unexpected geometry (record {recSize}, cluster {cluster}, sector {sector})";
            return false;
        }

        byte* buf = (byte*)NativeMemory.AlignedAlloc(ChunkSize, 4096);
        try
        {
            // Record 0 ($MFT itself) holds the data runs of the whole table.
            int rec0Read = AlignUp(recSize, sector);
            if (!ReadAt(volume, vd.MftStartLcn * cluster, buf, rec0Read) || !ApplyFixups(buf, recSize))
            {
                failure = "cannot read $MFT record 0";
                return false;
            }
            var runs = ReadDataRuns(buf, recSize);
            long mftBytes = vd.MftValidDataLength;
            if (runs == null || runs.Sum(r => r.Clusters) * cluster < mftBytes)
            {
                failure = "$MFT data runs incomplete (attribute list?)";
                return false;
            }

            long totalRecords = mftBytes / recSize;
            if (totalRecords > int.MaxValue) { failure = "MFT too large"; return false; }
            var attrsByRecord = new uint[totalRecords];   // SI attributes (+ directory bit) of base records
            var seqByRecord = new ushort[totalRecords];
            var ordinal = new byte[totalRecords];         // names added so far per base record
            List<(int Base, ulong Parent, string Name)>? pending = null; // names found in extension records

            // Chunk list (volume offset, bytes), then a two-buffer pipeline: the next chunk is read while this one is parsed.
            var chunks = new List<(long Offset, int Bytes)>();
            long planned = 0;
            foreach (var run in runs)
            {
                long runBytes = run.Clusters * cluster;
                for (long offset = 0; offset < runBytes && planned < totalRecords; )
                {
                    if (run.Lcn < 0) { failure = "sparse $MFT run"; return false; }
                    int want = (int)Math.Min(Math.Min(ChunkSize, runBytes - offset), (totalRecords - planned) * recSize);
                    int n = want / recSize;
                    if (n == 0) break;
                    chunks.Add((run.Lcn * cluster + offset, n * recSize));
                    planned += n;
                    offset += (long)n * recSize;
                }
            }

            byte* buf2 = (byte*)NativeMemory.AlignedAlloc(ChunkSize, 4096);
            Task<bool>? next = null;
            try
            {
                long recordIndex = 0;
                int lastReport = 0;
                if (chunks.Count > 0) next = StartRead(volume, chunks[0], buf, sector);
                for (int c = 0; c < chunks.Count; c++)
                {
                    ct.ThrowIfCancellationRequested();
                    byte* cur = (c & 1) == 0 ? buf : buf2;
                    if (!next!.GetAwaiter().GetResult()) { failure = $"volume read failed at {chunks[c].Offset}"; return false; }
                    next = c + 1 < chunks.Count ? StartRead(volume, chunks[c + 1], (c & 1) == 0 ? buf2 : buf, sector) : null;
                    int n = chunks[c].Bytes / recSize;
                    for (int k = 0; k < n; k++, recordIndex++)
                        ParseRecord(cur + (long)k * recSize, recSize, (int)recordIndex, rootFrn, raw, attrsByRecord, seqByRecord, ordinal, ref pending);
                    if (raw.Count - lastReport > 65536) { lastReport = raw.Count; progress?.Report(raw.Count); }
                }
            }
            finally
            {
                try { next?.Wait(); } catch { } // never leave a read running into a freed buffer
                NativeMemory.AlignedFree(buf2);
            }

            if (pending != null)
            {
                foreach (var (b, parent, name) in pending)
                {
                    if (b >= totalRecords || attrsByRecord[b] == 0) continue; // base not in use
                    ulong frn = ((ulong)seqByRecord[b] << 48) | (uint)b;
                    AddName(raw, frn, parent, attrsByRecord[b], name, ref ordinal[b]);
                }
            }
            return true;
        }
        finally { NativeMemory.AlignedFree(buf); }
    }

    /// <summary>
    /// Same result as <see cref="TryEnumerate"/>, but fetches each file record with FSCTL_GET_NTFS_FILE_RECORD, for
    /// volumes where raw reads are refused (seen on the system volume). The control code returns the nearest in-use
    /// record at or below the requested number, so walking downwards skips free records; ranges run in parallel,
    /// one volume handle per worker (I/O on one synchronous handle is serialized).
    /// </summary>
    public static bool TryEnumerateByRecord(string volumePath, ulong rootFrn, RawRecords raw, IProgress<long>? progress, CancellationToken ct, out string? failure)
    {
        failure = null;
        NTFS_VOLUME_DATA_BUFFER vd;
        using (var h0 = OpenVolume(volumePath))
        {
            if (h0.IsInvalid) { failure = $"open failed ({Marshal.GetLastWin32Error()})"; return false; }
            if (!Kernel32.DeviceIoControl(h0, UsnNative.FSCTL_GET_NTFS_VOLUME_DATA, null, 0, &vd, sizeof(NTFS_VOLUME_DATA_BUFFER), out _, IntPtr.Zero))
            {
                failure = $"FSCTL_GET_NTFS_VOLUME_DATA failed ({Marshal.GetLastWin32Error()})";
                return false;
            }
        }
        int recSize = (int)vd.BytesPerFileRecordSegment;
        long total = vd.MftValidDataLength / Math.Max(1, recSize);
        if (recSize < 1024 || recSize > 4096 || total > int.MaxValue) { failure = "unexpected geometry"; return false; }

        var attrsByRecord = new uint[total];
        var seqByRecord = new ushort[total];
        var ordinal = new byte[total];
        int workers = Math.Clamp(Environment.ProcessorCount / 2, 2, 8);
        var locals = new RawRecords[workers];
        var pendings = new List<(int, ulong, string)>?[workers];
        var errors = new string?[workers];
        long done = 0;
        Parallel.For(0, workers, new ParallelOptions { MaxDegreeOfParallelism = workers, CancellationToken = ct }, w =>
        {
            long lo = total * w / workers, hi = total * (w + 1) / workers;
            var local = new RawRecords((int)Math.Max(1024, (hi - lo) / 2));
            List<(int, ulong, string)>? pending = null;
            int outSize = 12 + recSize;
            byte* outBuf = (byte*)NativeMemory.Alloc((nuint)outSize);
            try
            {
                using var h = OpenVolume(volumePath);
                if (h.IsInvalid) { errors[w] = $"open failed ({Marshal.GetLastWin32Error()})"; return; }
                long i = hi - 1, sinceReport = 0;
                while (i >= lo)
                {
                    if ((sinceReport & 0x3FFF) == 0) ct.ThrowIfCancellationRequested();
                    long req = i;
                    if (!Kernel32.DeviceIoControl(h, UsnNative.FSCTL_GET_NTFS_FILE_RECORD, &req, 8, outBuf, outSize, out _, IntPtr.Zero))
                    {
                        errors[w] = $"FSCTL_GET_NTFS_FILE_RECORD({i}) failed ({Marshal.GetLastWin32Error()})";
                        return;
                    }
                    long r = *(long*)outBuf & 0x0000FFFFFFFFFFFF;
                    if (r < lo || r > i) break;
                    int len = *(int*)(outBuf + 8);
                    if (len == recSize)
                        ParseRecord(outBuf + 12, recSize, (int)r, rootFrn, local, attrsByRecord, seqByRecord, ordinal, ref pending, strictFixups: false);
                    i = r - 1;
                    if (++sinceReport % 65536 == 0) progress?.Report(Interlocked.Add(ref done, 65536));
                }
            }
            finally { NativeMemory.Free(outBuf); }
            locals[w] = local;
            pendings[w] = pending;
        });
        foreach (var e in errors) if (e != null) { failure = e; return false; }
        foreach (var l in locals) raw.AddAll(l);
        foreach (var p in pendings)
        {
            if (p == null) continue;
            foreach (var (b, parent, name) in p)
            {
                if (b >= total || attrsByRecord[b] == 0) continue;
                ulong frn = ((ulong)seqByRecord[b] << 48) | (uint)b;
                AddName(raw, frn, parent, attrsByRecord[b], name, ref ordinal[b]);
            }
        }
        return true;
    }

    private static SafeFileHandle OpenVolume(string volumePath) =>
        Kernel32.CreateFile(volumePath, Kernel32.GENERIC_READ, Kernel32.FILE_SHARE_READ | Kernel32.FILE_SHARE_WRITE, IntPtr.Zero, Kernel32.OPEN_EXISTING, 0, IntPtr.Zero);

    private static int AlignUp(int v, int a) => (v + a - 1) / a * a;

    private static Task<bool> StartRead(SafeFileHandle h, (long Offset, int Bytes) chunk, byte* target, int sector)
    {
        IntPtr p = (IntPtr)target;
        return Task.Run(() => ReadAt(h, chunk.Offset, (byte*)p, AlignUp(chunk.Bytes, sector)));
    }

    private static bool ReadAt(SafeFileHandle h, long offset, byte* buf, int count)
    {
        try
        {
            int total = 0;
            while (total < count)
            {
                int r = RandomAccess.Read(h, new Span<byte>(buf + total, count - total), offset + total);
                if (r <= 0) return false;
                total += r;
            }
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    /// <summary>Validates the "FILE" header and replaces the update-sequence placeholders at the end of each 512-byte stride.</summary>
    private static bool ApplyFixups(byte* rec, int recSize, bool strict = true)
    {
        if (*(uint*)rec != FileSignature) return false;
        int usaOff = *(ushort*)(rec + 4);
        int usaCount = *(ushort*)(rec + 6);
        if (usaCount < 2 || usaOff + usaCount * 2 > recSize || (usaCount - 1) * FixupStride > recSize) return false;
        ushort usn = *(ushort*)(rec + usaOff);
        for (int i = 1; i < usaCount; i++)
        {
            ushort* tail = (ushort*)(rec + i * FixupStride - 2);
            if (*tail != usn)
            {
                if (strict) return false; // torn / corrupt record
                continue;                 // FSCTL_GET_NTFS_FILE_RECORD may hand out an already fixed-up copy
            }
            *tail = *(ushort*)(rec + usaOff + i * 2);
        }
        return true;
    }

    private static List<Run>? ReadDataRuns(byte* rec, int recSize)
    {
        int a = *(ushort*)(rec + 0x14);
        while (a + 8 <= recSize)
        {
            uint type = *(uint*)(rec + a);
            if (type == AttrEnd) break;
            int len = (int)*(uint*)(rec + a + 4);
            if (len <= 0 || a + len > recSize) return null;
            if (type == AttrData && rec[a + 8] != 0 && rec[a + 9] == 0 && *(long*)(rec + a + 16) == 0)
            {
                var runs = new List<Run>();
                byte* p = rec + a + *(ushort*)(rec + a + 32);
                byte* end = rec + a + len;
                long lcn = 0;
                while (p < end && *p != 0)
                {
                    int lenSize = *p & 0xF, offSize = *p >> 4;
                    p++;
                    if (lenSize == 0 || lenSize > 8 || offSize > 8 || p + lenSize + offSize > end) return null;
                    long clusters = 0;
                    for (int i = 0; i < lenSize; i++) clusters |= (long)p[i] << (8 * i);
                    p += lenSize;
                    if (offSize == 0) { runs.Add(new Run(-1, clusters)); continue; } // sparse
                    long delta = 0;
                    for (int i = 0; i < offSize; i++) delta |= (long)p[i] << (8 * i);
                    if ((p[offSize - 1] & 0x80) != 0 && offSize < 8) delta -= 1L << (8 * offSize); // sign-extend
                    p += offSize;
                    lcn += delta;
                    runs.Add(new Run(lcn, clusters));
                }
                return runs;
            }
            a += len;
        }
        return null;
    }

    private static void ParseRecord(byte* rec, int recSize, int index, ulong rootFrn, RawRecords raw,
        uint[] attrsByRecord, ushort[] seqByRecord, byte[] ordinal, ref List<(int, ulong, string)>? pending, bool strictFixups = true)
    {
        if (!ApplyFixups(rec, recSize, strictFixups)) return;
        ushort flags = *(ushort*)(rec + 0x16);
        if ((flags & RecordInUse) == 0) return;
        ulong baseRef = *(ulong*)(rec + 0x20);
        int baseIndex = (int)(baseRef & 0x0000FFFFFFFFFFFF);
        bool isExtension = baseRef != 0;
        ushort seq = *(ushort*)(rec + 0x10);
        if (!isExtension)
        {
            seqByRecord[index] = seq;
            if (index < 24) return; // NTFS metafiles
        }
        ulong frn = ((ulong)seq << 48) | (uint)index;
        if (frn == rootFrn) return;

        uint attrs = 0;
        int a = *(ushort*)(rec + 0x14);
        while (a + 8 <= recSize)
        {
            uint type = *(uint*)(rec + a);
            if (type == AttrEnd || type > AttrFileName) break; // attributes are sorted by type
            int len = (int)*(uint*)(rec + a + 4);
            if (len <= 0 || a + len > recSize) return;
            if (rec[a + 8] == 0) // resident
            {
                int vlen = (int)*(uint*)(rec + a + 16);
                int voff = *(ushort*)(rec + a + 20);
                byte* v = rec + a + voff;
                if (voff + vlen > len) return;
                if (type == AttrStandardInformation && !isExtension && vlen >= 0x24)
                {
                    attrs = *(uint*)(v + 0x20);
                    if ((flags & RecordIsDirectory) != 0) attrs |= UsnNative.FILE_ATTRIBUTE_DIRECTORY;
                    else attrs &= ~UsnNative.FILE_ATTRIBUTE_DIRECTORY;
                    attrs |= 0x80000000; // marks "in use" for the pending pass (masked off below)
                    attrsByRecord[index] = attrs;
                }
                else if (type == AttrFileName && vlen >= 0x42)
                {
                    byte nameLen = v[0x40];
                    byte ns = v[0x41];
                    if (ns == 2 || 0x42 + nameLen * 2 > vlen) { a += len; continue; } // DOS 8.3 alias
                    ulong parent = *(ulong*)v;
                    var name = new ReadOnlySpan<char>(v + 0x42, nameLen);
                    if (isExtension)
                    {
                        pending ??= new List<(int, ulong, string)>();
                        pending.Add((baseIndex, parent, name.ToString()));
                    }
                    else if (attrs != 0)
                        AddName(raw, frn, parent, attrs, name, ref ordinal[index]);
                }
            }
            a += len;
        }
    }

    private static void AddName(RawRecords raw, ulong frn, ulong parent, uint attrs, ReadOnlySpan<char> name, ref byte ordinal)
    {
        attrs &= 0x7FFFFFFF;
        if (ordinal == 0) raw.Add(frn, parent, attrs, name);
        else if (ordinal <= LinkKey.MaxExtra && (attrs & UsnNative.FILE_ATTRIBUTE_DIRECTORY) == 0 && LinkKey.CanEncode(frn))
            raw.Add(LinkKey.Extra(frn, ordinal), parent, attrs, name);
        if (ordinal < 255) ordinal++;
    }
}

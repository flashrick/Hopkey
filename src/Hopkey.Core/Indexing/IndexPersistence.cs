using System.Runtime.InteropServices;
using System.Text;
using Hopkey.Core.Util;

namespace Hopkey.Core.Indexing;

/// <summary>
/// index.bin: raw array dumps (no serializer). Lower arena and pinyin are NOT stored; they're recomputed in parallel on load.
/// Layout: "CLIX" | int version | string fingerprint | int volumeCount | volumes…
/// </summary>
public static class IndexPersistence
{
    private const uint Magic = 0x58494C43; // "CLIX"
    // 3: USN volumes include every hard-link name (extra links keyed by LinkKey); v2 USN caches lack them → rebuild.
    private const int Version = 3;

    public sealed record LoadResult(string Fingerprint, List<VolumeIndex> Volumes);

    public static void Save(string file, string fingerprint, IReadOnlyList<VolumeIndex> volumes)
    {
        var tmp = file + ".tmp";
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, FileOptions.SequentialScan))
        using (var bw = new BinaryWriter(fs, Encoding.UTF8, leaveOpen: true))
        {
            bw.Write(Magic);
            bw.Write(Version);
            bw.Write(fingerprint);
            bw.Write(volumes.Count);
            foreach (var v in volumes)
            {
                v.Lock.EnterReadLock();
                try
                {
                    int n = v.CountInternal;
                    bw.Write(v.RootPath);
                    bw.Write((byte)v.Kind);
                    bw.Write(v.VolumeSerial);
                    bw.Write(v.JournalId);
                    bw.Write(v.NextUsn);
                    bw.Write(v.LastUpdatedUtc.Ticks);
                    bw.Write(n);
                    bw.Write(v.Names.Length);
                    bw.Flush();
                    fs.Write(MemoryMarshal.AsBytes(v.Parent.AsSpan(0, n)));
                    fs.Write(MemoryMarshal.AsBytes(v.NameOffset.AsSpan(0, n + 1)));
                    fs.Write(v.NameLength.AsSpan(0, n));
                    fs.Write(MemoryMarshal.AsBytes(v.Flags.AsSpan(0, n)));
                    fs.Write(MemoryMarshal.AsBytes(v.Names.Data.AsSpan(0, v.Names.Length)));
                    if (v.Kind == IndexSourceKind.Usn) fs.Write(MemoryMarshal.AsBytes(v.Frn!.AsSpan(0, n)));
                    v.Dirty = false;
                }
                finally { v.Lock.ExitReadLock(); }
            }
            bw.Flush();
            fs.Flush(true);
        }
        if (File.Exists(file)) File.Replace(tmp, file, null, true);
        else File.Move(tmp, file);
    }

    public static LoadResult? Load(string file)
    {
        if (!File.Exists(file)) return null;
        try
        {
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
            using var br = new BinaryReader(fs, Encoding.UTF8, leaveOpen: true);
            if (br.ReadUInt32() != Magic || br.ReadInt32() != Version) return null;
            var fingerprint = br.ReadString();
            int count = br.ReadInt32();
            var list = new List<VolumeIndex>(count);
            for (int k = 0; k < count; k++)
            {
                var root = br.ReadString();
                var kind = (IndexSourceKind)br.ReadByte();
                uint serial = br.ReadUInt32();
                ulong journal = br.ReadUInt64();
                long nextUsn = br.ReadInt64();
                long ticks = br.ReadInt64();
                int n = br.ReadInt32();
                int arenaLen = br.ReadInt32();
                int slack = Math.Max(1024, n / 50);
                var parent = new int[n + slack];
                var nameOffset = new int[n + slack + 1];
                var nameLen = new byte[n + slack];
                var flags = new IndexEntryFlags[n + slack];
                var names = new char[arenaLen + Math.Max(16 * 1024, arenaLen / 50)];
                fs.ReadExactly(MemoryMarshal.AsBytes(parent.AsSpan(0, n)));
                fs.ReadExactly(MemoryMarshal.AsBytes(nameOffset.AsSpan(0, n + 1)));
                fs.ReadExactly(nameLen.AsSpan(0, n));
                fs.ReadExactly(MemoryMarshal.AsBytes(flags.AsSpan(0, n)));
                fs.ReadExactly(MemoryMarshal.AsBytes(names.AsSpan(0, arenaLen)));
                ulong[]? frn = null;
                if (kind == IndexSourceKind.Usn)
                {
                    frn = new ulong[n + slack];
                    fs.ReadExactly(MemoryMarshal.AsBytes(frn.AsSpan(0, n)));
                }
                if (kind == IndexSourceKind.Usn && serial != 0 && UsnIndexSource.GetVolumeSerial(root) != serial)
                {
                    Logger.Info($"Volume serial changed for {root}; dropping cached index");
                    continue;
                }
                var v = VolumeIndex.FromArrays(root, kind, n, parent, nameOffset, nameLen, flags, frn, names, arenaLen);
                v.VolumeSerial = serial;
                v.JournalId = journal;
                v.NextUsn = nextUsn;
                v.LastUpdatedUtc = new DateTime(ticks, DateTimeKind.Utc);
                v.Dirty = false;
                list.Add(v);
            }
            return new LoadResult(fingerprint, list);
        }
        catch (Exception ex)
        {
            Logger.Warn("Failed to load index cache; it will be rebuilt", ex);
            return null;
        }
    }
}

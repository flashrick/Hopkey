using System.Threading.Channels;
using Hopkey.Core.Exclusion;

namespace Hopkey.Core.Indexing;

public interface IIndexSource : IDisposable
{
    string RootPath { get; }
    IndexSourceKind Kind { get; }

    /// <summary>Full build.</summary>
    Task<VolumeIndex> BuildAsync(ExclusionMatcher matcher, IProgress<long>? progress, CancellationToken ct);

    /// <summary>
    /// Continue from a cached index. USN replays the journal and returns true on success.
    /// Crawler returns false (the caller keeps the cache searchable and rebuilds in background).
    /// </summary>
    Task<bool> CatchUpAsync(VolumeIndex cached, ExclusionMatcher matcher, CancellationToken ct);

    void StartWatching(ChannelWriter<(string Root, IndexChange Change)> sink);
    void StopWatching();

    /// <summary>Raised when the live feed is broken (journal wrapped/ID changed, FSW buffer overflow).</summary>
    event Action<string>? RebuildRequired;
}

/// <summary>Raw records produced by MFT enumeration, before parent resolution and exclusion.</summary>
internal sealed class RawRecords
{
    public int Count;
    public ulong[] Frn;
    public ulong[] ParentFrn;
    public uint[] Attrs;
    public int[] NameOff;
    public byte[] NameLen;
    public char[] Arena;
    public int ArenaLength;

    public RawRecords(int capacity = 1 << 16)
    {
        Frn = new ulong[capacity];
        ParentFrn = new ulong[capacity];
        Attrs = new uint[capacity];
        NameOff = new int[capacity];
        NameLen = new byte[capacity];
        Arena = new char[capacity * 16];
    }

    public void Add(ulong frn, ulong parent, uint attrs, ReadOnlySpan<char> name)
    {
        if (Count == Frn.Length)
        {
            int cap = Count * 2;
            Array.Resize(ref Frn, cap);
            Array.Resize(ref ParentFrn, cap);
            Array.Resize(ref Attrs, cap);
            Array.Resize(ref NameOff, cap);
            Array.Resize(ref NameLen, cap);
        }
        if (name.Length > 255) name = name[..255];
        if (ArenaLength + name.Length > Arena.Length)
            Array.Resize(ref Arena, Math.Max(Arena.Length * 2, ArenaLength + name.Length + 1024));
        name.CopyTo(Arena.AsSpan(ArenaLength));
        Frn[Count] = frn;
        ParentFrn[Count] = parent;
        Attrs[Count] = attrs;
        NameOff[Count] = ArenaLength;
        NameLen[Count] = (byte)name.Length;
        ArenaLength += name.Length;
        Count++;
    }

    public ReadOnlySpan<char> Name(int i) => new(Arena, NameOff[i], NameLen[i]);

    public void AddAll(RawRecords other)
    {
        for (int i = 0; i < other.Count; i++) Add(other.Frn[i], other.ParentFrn[i], other.Attrs[i], other.Name(i));
    }
}

using System.Runtime.CompilerServices;

namespace Hopkey.Core.Indexing;

/// <summary>
/// Growable char arena: "\0name\0name\0…". Offsets are stable until compaction.
/// Not thread-safe; owners synchronize.
/// </summary>
internal sealed class NameArena
{
    public char[] Data;
    public int Length;

    public NameArena(int capacity = 1024)
    {
        Data = new char[Math.Max(capacity, 16)];
        Data[0] = '\0';
        Length = 1;
    }

    public NameArena(char[] data, int length)
    {
        Data = data;
        Length = length;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void EnsureCapacity(int extra)
    {
        if (Length + extra <= Data.Length) return;
        int newCap = Math.Max(Length + extra, (int)Math.Min((long)Data.Length * 3 / 2, Array.MaxLength));
        Array.Resize(ref Data, newCap);
    }

    /// <summary>Append a name followed by '\0'. Returns the start offset of the name.</summary>
    public int Append(ReadOnlySpan<char> name)
    {
        EnsureCapacity(name.Length + 1);
        int off = Length;
        name.CopyTo(Data.AsSpan(off));
        Data[off + name.Length] = '\0';
        Length = off + name.Length + 1;
        return off;
    }

    public ReadOnlySpan<char> Slice(int offset, int length) => new(Data, offset, length);

    /// <summary>Lowercase copy of a char range using the same rule as query normalization.</summary>
    public static void LowerInto(ReadOnlySpan<char> src, Span<char> dst)
    {
        for (int i = 0; i < src.Length; i++)
        {
            char c = src[i];
            dst[i] = c < 128 ? (c is >= 'A' and <= 'Z' ? (char)(c | 0x20) : c) : char.ToLowerInvariant(c);
        }
    }

    /// <summary>Parallel lowercase of an entire arena (used on load / after build).</summary>
    public static char[] BuildLower(char[] names, int length)
    {
        var lower = new char[names.Length];
        const int Chunk = 1 << 20;
        int chunks = (length + Chunk - 1) / Chunk;
        Parallel.For(0, chunks, c =>
        {
            int start = c * Chunk;
            int len = Math.Min(Chunk, length - start);
            LowerInto(new ReadOnlySpan<char>(names, start, len), new Span<char>(lower, start, len));
        });
        return lower;
    }
}

/// <summary>Pinyin initials for entries whose name contains CJK. EntryIds strictly increasing.</summary>
internal sealed class PinyinArena
{
    public NameArena Chars = new(4096);
    public int[] Offsets = new int[256];
    public int[] EntryIds = new int[256];
    public int Count;

    public void Add(int entryId, ReadOnlySpan<char> name)
    {
        if (Count == Offsets.Length)
        {
            Array.Resize(ref Offsets, Count * 2);
            Array.Resize(ref EntryIds, Count * 2);
        }
        Span<char> buf = name.Length <= 256 ? stackalloc char[name.Length] : new char[name.Length];
        Pinyin.PinyinHelper.WriteInitials(name, buf);
        Offsets[Count] = Chars.Append(buf);
        EntryIds[Count] = entryId;
        Count++;
    }

    /// <summary>Find the pinyin entry slot containing arena offset (binary search on Offsets).</summary>
    public int SlotAtOffset(int offset)
    {
        int lo = 0, hi = Count - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) >> 1;
            if (Offsets[mid] <= offset) lo = mid; else hi = mid - 1;
        }
        return lo;
    }
}

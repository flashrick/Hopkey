namespace Hopkey.Core.Indexing;

[Flags]
public enum IndexEntryFlags : byte
{
    None = 0,
    Directory = 1,
    Hidden = 2,
    System = 4,
    ReparsePoint = 8,
    Deleted = 16,
    HasCjk = 32,
}

public enum IndexSourceKind : byte { Usn = 1, Crawler = 2 }

public enum ChangeKind : byte { Created, Deleted, Renamed, AttributesChanged, HardLinkChanged }

/// <summary>
/// Keys for the extra names of a hard-linked file (USN volumes). The first name uses the real file reference number;
/// link n (1..127) uses FRN | bit 47 | n &lt;&lt; 40. Record numbers stay far below 2^40, so these never collide with a
/// real FRN, and only files (never directories, which can't be hard-linked) get them, so they are never a parent.
/// </summary>
internal static class LinkKey
{
    public const ulong Marker = 1UL << 47;
    private const ulong OrdinalMask = 0x7FUL << 40;
    public const int MaxExtra = 127;
    public static bool CanEncode(ulong frn) => (frn & (0xFFUL << 40)) == 0;
    public static ulong Extra(ulong frn, int ordinal) => frn | Marker | ((ulong)ordinal << 40);
    public static bool IsExtra(ulong key) => (key & Marker) != 0;
    public static ulong Base(ulong key) => key & ~(Marker | OrdinalMask);
}

/// <summary>A single file-system change. USN sources use Key/ParentKey; crawler sources use FullPath/OldFullPath.</summary>
public readonly record struct IndexChange(
    ChangeKind Kind,
    ulong Key,
    ulong ParentKey,
    string? Name,
    FileAttributes Attributes,
    string? FullPath,
    string? OldFullPath);

public enum IndexState { Idle, Loading, Building, Ready, Paused, Error }

public sealed record IndexStatus(IndexState State, long Count, string? Message = null, int VolumesBuilding = 0)
{
    public static IndexStatus Initial { get; } = new(IndexState.Idle, 0);
}

public sealed record VolumeStats(string Root, IndexSourceKind Kind, int LiveCount, DateTime LastUpdatedUtc, TimeSpan? LastBuildTime);

internal static class FlagsUtil
{
    public static IndexEntryFlags FromAttributes(FileAttributes a)
    {
        var f = IndexEntryFlags.None;
        if ((a & FileAttributes.Directory) != 0) f |= IndexEntryFlags.Directory;
        if ((a & FileAttributes.Hidden) != 0) f |= IndexEntryFlags.Hidden;
        if ((a & FileAttributes.System) != 0) f |= IndexEntryFlags.System;
        if ((a & FileAttributes.ReparsePoint) != 0) f |= IndexEntryFlags.ReparsePoint;
        return f;
    }

    public static FileAttributes ToAttributes(IndexEntryFlags f)
    {
        FileAttributes a = 0;
        if ((f & IndexEntryFlags.Directory) != 0) a |= FileAttributes.Directory;
        if ((f & IndexEntryFlags.Hidden) != 0) a |= FileAttributes.Hidden;
        if ((f & IndexEntryFlags.System) != 0) a |= FileAttributes.System;
        if ((f & IndexEntryFlags.ReparsePoint) != 0) a |= FileAttributes.ReparsePoint;
        return a;
    }
}

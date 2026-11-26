using System.Runtime.InteropServices;

namespace Hopkey.Core.Native;

public static class UsnNative
{
    public const uint FSCTL_QUERY_USN_JOURNAL = 0x000900F4;
    public const uint FSCTL_ENUM_USN_DATA = 0x000900B3;
    public const uint FSCTL_READ_USN_JOURNAL = 0x000900BB;
    public const uint FSCTL_QUERY_FILE_LAYOUT = 0x00090277;
    public const uint FSCTL_GET_NTFS_VOLUME_DATA = 0x00090064;
    public const uint FSCTL_GET_NTFS_FILE_RECORD = 0x00090068;

    // QUERY_FILE_LAYOUT_INPUT.Flags
    public const uint QUERY_FILE_LAYOUT_RESTART = 0x1;
    public const uint QUERY_FILE_LAYOUT_INCLUDE_NAMES = 0x2;
    // FILE_LAYOUT_NAME_ENTRY.Flags
    public const uint FILE_LAYOUT_NAME_ENTRY_PRIMARY = 0x1;
    public const uint FILE_LAYOUT_NAME_ENTRY_DOS = 0x2;
    public const int QueryFileLayoutInputSize = 32;
    // QUERY_FILE_LAYOUT_OUTPUT: FileEntryCount @0, FirstFileOffset @4
    // FILE_LAYOUT_ENTRY: NextFileOffset @4, FileAttributes @12, FileReferenceNumber @16, FirstNameOffset @24
    public const int FLE_NextFileOffset = 4, FLE_Attributes = 12, FLE_Frn = 16, FLE_FirstNameOffset = 24;
    // FILE_LAYOUT_NAME_ENTRY: NextNameOffset @0, Flags @4, ParentFileReferenceNumber @8, FileNameLength @16, FileName @24
    public const int FLN_Next = 0, FLN_Flags = 4, FLN_Parent = 8, FLN_NameLength = 16, FLN_Name = 24;

    public const uint USN_REASON_DATA_OVERWRITE = 0x00000001;
    public const uint USN_REASON_FILE_CREATE = 0x00000100;
    public const uint USN_REASON_FILE_DELETE = 0x00000200;
    public const uint USN_REASON_RENAME_OLD_NAME = 0x00001000;
    public const uint USN_REASON_RENAME_NEW_NAME = 0x00002000;
    public const uint USN_REASON_BASIC_INFO_CHANGE = 0x00008000;
    public const uint USN_REASON_HARD_LINK_CHANGE = 0x00010000;
    public const uint USN_REASON_CLOSE = 0x80000000;

    public const uint FILE_ATTRIBUTE_READONLY = 0x1;
    public const uint FILE_ATTRIBUTE_HIDDEN = 0x2;
    public const uint FILE_ATTRIBUTE_SYSTEM = 0x4;
    public const uint FILE_ATTRIBUTE_DIRECTORY = 0x10;
    public const uint FILE_ATTRIBUTE_REPARSE_POINT = 0x400;

    // Offsets inside USN_RECORD_V2 / V3
    public const int RecordLengthOffset = 0;
    public const int MajorVersionOffset = 4;

    // V2
    public const int V2_FrnOffset = 8;
    public const int V2_ParentFrnOffset = 16;
    public const int V2_UsnOffset = 24;
    public const int V2_ReasonOffset = 40;
    public const int V2_AttributesOffset = 52;
    public const int V2_FileNameLengthOffset = 56;
    public const int V2_FileNameOffsetOffset = 58;

    // V3 (128-bit file ids)
    public const int V3_FrnOffset = 8;          // 16 bytes
    public const int V3_ParentFrnOffset = 24;   // 16 bytes
    public const int V3_UsnOffset = 40;
    public const int V3_ReasonOffset = 56;
    public const int V3_AttributesOffset = 68;
    public const int V3_FileNameLengthOffset = 72;
    public const int V3_FileNameOffsetOffset = 74;
}

[StructLayout(LayoutKind.Sequential)]
public struct USN_JOURNAL_DATA_V0
{
    public ulong UsnJournalID;
    public long FirstUsn;
    public long NextUsn;
    public long LowestValidUsn;
    public long MaxUsn;
    public ulong MaximumSize;
    public ulong AllocationDelta;
}

[StructLayout(LayoutKind.Sequential)]
public struct NTFS_VOLUME_DATA_BUFFER
{
    public long VolumeSerialNumber;
    public long NumberSectors;
    public long TotalClusters;
    public long FreeClusters;
    public long TotalReserved;
    public uint BytesPerSector;
    public uint BytesPerCluster;
    public uint BytesPerFileRecordSegment;
    public uint ClustersPerFileRecordSegment;
    public long MftValidDataLength;
    public long MftStartLcn;
    public long Mft2StartLcn;
    public long MftZoneStart;
    public long MftZoneEnd;
}

[StructLayout(LayoutKind.Sequential)]
public struct MFT_ENUM_DATA_V0
{
    public ulong StartFileReferenceNumber;
    public long LowUsn;
    public long HighUsn;
}

[StructLayout(LayoutKind.Sequential)]
public struct READ_USN_JOURNAL_DATA_V0
{
    public long StartUsn;
    public uint ReasonMask;
    public uint ReturnOnlyOnClose;
    public ulong Timeout;
    public ulong BytesToWaitFor;
    public ulong UsnJournalID;
}

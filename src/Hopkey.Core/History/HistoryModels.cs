namespace Hopkey.Core.History;

public enum FolderVisitSource { Explorer, Dialog, Launcher }

public enum UsageKind { File, Folder, App, Web, Calculator, Command }

public sealed class UsageRecord
{
    public string Key { get; set; } = "";
    public UsageKind Kind { get; set; }
    public int Count { get; set; }
    public DateTime LastUsedUtc { get; set; }
    /// <summary>Normalized query → count. Max 10 entries.</summary>
    public Dictionary<string, int> Queries { get; set; } = new();
}

public sealed class RecentFolder
{
    public string Path { get; set; } = "";
    public DateTime LastVisitUtc { get; set; }
    public FolderVisitSource Source { get; set; }
}

public sealed class HistoryData
{
    public int Version { get; set; } = 1;
    public List<UsageRecord> Usage { get; set; } = new();
    public List<RecentFolder> RecentFolders { get; set; } = new();
}

using Hopkey.Core.Config;

namespace Hopkey.Core.Search;

public enum ResultKind { File, Folder, App, Web, Calculator, Command }

public enum TypeFilter { Any, Folder, File, App }

public sealed record SearchQuery(
    string Raw,
    IReadOnlyList<string> Keywords,
    TypeFilter Type,
    string[]? Extensions,
    WebSearchEngine? Web,
    string? WebText,
    string? Expression)
{
    public bool IsEmpty => Keywords.Count == 0 && Web == null && Expression == null && Type == TypeFilter.Any && Extensions == null;
    /// <summary>Normalized (trimmed, lowercased) raw text, used for history associations.</summary>
    public string Normalized { get; init; } = "";
}

public sealed class SearchResult
{
    public ResultKind Kind { get; init; }
    public string Title { get; init; } = "";
    public string? Subtitle { get; init; }
    public string? Path { get; init; }
    public string? Arguments { get; init; }
    public IReadOnlyList<(int Start, int Length)> Highlights { get; init; } = Array.Empty<(int, int)>();
    public double Score { get; set; }
    public string? CopyText { get; init; }
    /// <summary>Group label for empty-query views ("收藏", "最近使用", "最近文件夹").</summary>
    public string? Group { get; init; }
    public bool IsFavorite { get; init; }
    public bool IsUwp { get; init; }

    public bool IsFileSystem => Kind is ResultKind.File or ResultKind.Folder || (Kind == ResultKind.App && !IsUwp);
    public string Key => Path ?? Title;

    public override string ToString() => $"{Kind} {Title} ({Score:F0}) {Path}";
}

/// <summary>Group labels; the UI may localize them.</summary>
public static class ResultGroups
{
    public const string Favorites = "favorites";
    public const string Recent = "recent";
    public const string RecentFolders = "recentFolders";
}

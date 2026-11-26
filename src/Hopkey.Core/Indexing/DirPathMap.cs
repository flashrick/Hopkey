namespace Hopkey.Core.Indexing;

/// <summary>
/// Directory full path → entry index, keyed by a 64-bit case-insensitive hash instead of the path string
/// (~24 B/dir instead of ~250 B/dir). Callers must verify hits (VolumeIndex.TryFindDirectory compares the
/// reconstructed path), which also makes stale keys after directory renames harmless.
/// </summary>
internal sealed class DirPathMap
{
    private readonly Dictionary<ulong, int> _map = new();

    public int Count => _map.Count;

    public static ulong Hash(ReadOnlySpan<char> path)
    {
        // Trim a trailing separator (except "C:\") so "C:\x\" and "C:\x" hash alike.
        if (path.Length > 3 && (path[^1] == '\\' || path[^1] == '/')) path = path[..^1];
        ulong h = 14695981039346656037UL;
        foreach (char c0 in path)
        {
            char c = c0 == '/' ? '\\' : c0 < 128 ? (c0 is >= 'A' and <= 'Z' ? (char)(c0 | 0x20) : c0) : char.ToLowerInvariant(c0);
            h = (h ^ c) * 1099511628211UL;
        }
        return h;
    }

    public int this[string path]
    {
        set => _map[Hash(path)] = value;
    }

    public bool TryGetValue(string path, out int index) => _map.TryGetValue(Hash(path), out index);

    public bool Remove(string path) => _map.Remove(Hash(path));

    public void Clear() => _map.Clear();
}

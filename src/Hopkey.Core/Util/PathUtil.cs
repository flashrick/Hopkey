namespace Hopkey.Core.Util;

public static class PathUtil
{
    public static readonly StringComparer Comparer = StringComparer.OrdinalIgnoreCase;

    /// <summary>Expand env vars, make absolute, trim trailing separator (except for drive roots).</summary>
    public static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        var p = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"')).Replace('/', '\\');
        if (p.StartsWith("::", StringComparison.Ordinal) || p.StartsWith("shell:", StringComparison.OrdinalIgnoreCase)) return p;
        try { p = Path.GetFullPath(p); } catch { }
        return TrimEndSeparator(p);
    }

    public static string TrimEndSeparator(string p)
    {
        if (p.Length > 3 && (p[^1] == '\\' || p[^1] == '/')) return p.TrimEnd('\\', '/');
        if (p.Length == 2 && p[1] == ':') return p + "\\";
        return p;
    }

    public static string EnsureEndSeparator(string p) => p.EndsWith('\\') ? p : p + "\\";

    public static bool Equal(string? a, string? b)
    {
        if (a == null || b == null) return false;
        return string.Equals(TrimEndSeparator(a), TrimEndSeparator(b), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True if <paramref name="path"/> equals <paramref name="root"/> or is inside it.</summary>
    public static bool IsUnder(string path, string root)
    {
        root = TrimEndSeparator(root);
        path = TrimEndSeparator(path);
        if (path.Length < root.Length) return false;
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return false;
        if (path.Length == root.Length) return true;
        return root.EndsWith('\\') || path[root.Length] == '\\';
    }

    public static bool IsFileSystemPath(string? p) =>
        !string.IsNullOrEmpty(p) && p.Length >= 2 &&
        ((p[1] == ':' && char.IsAsciiLetter(p[0])) || p.StartsWith(@"\\", StringComparison.Ordinal));

    public static bool IsNetworkPath(string p) => p.StartsWith(@"\\", StringComparison.Ordinal);

    /// <summary>Directory existence check with timeout (network paths may hang).</summary>
    public static async Task<bool> DirectoryExistsAsync(string path, TimeSpan timeout)
    {
        if (!IsNetworkPath(path)) return Directory.Exists(path);
        var t = Task.Run(() => Directory.Exists(path));
        var done = await Task.WhenAny(t, Task.Delay(timeout)).ConfigureAwait(false);
        return done == t && t.Result;
    }

    public static bool DirectoryExistsFast(string path, int timeoutMs = 200)
    {
        if (!IsNetworkPath(path)) return Directory.Exists(path);
        var t = Task.Run(() => Directory.Exists(path));
        return t.Wait(timeoutMs) && t.Result;
    }

    public static string GetDisplayName(string path)
    {
        var p = TrimEndSeparator(path);
        if (p.Length <= 3) return p;
        var name = Path.GetFileName(p);
        return string.IsNullOrEmpty(name) ? p : name;
    }

    /// <summary>Shorten a path for display by replacing the middle with an ellipsis.</summary>
    public static string Ellipsize(string path, int max)
    {
        if (path.Length <= max || max < 8) return path;
        int keepEnd = max / 2;
        int keepStart = max - keepEnd - 1;
        return string.Concat(path.AsSpan(0, keepStart), "…", path.AsSpan(path.Length - keepEnd));
    }

    public static string? GetParent(string path)
    {
        try { return Path.GetDirectoryName(TrimEndSeparator(path)); } catch { return null; }
    }
}

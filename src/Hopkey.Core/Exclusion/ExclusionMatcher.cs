using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Hopkey.Core.Config;
using Hopkey.Core.Util;

namespace Hopkey.Core.Exclusion;

public sealed record ExclusionDiff(bool HasAdditions, bool HasRemovals, IReadOnlyList<string> RemovedPathRules, bool RemovedNonPathRules)
{
    public bool IsEmpty => !HasAdditions && !HasRemovals;
}

/// <summary>
/// Compiled, immutable, thread-safe exclusion matcher shared by the indexer and search.
/// Cost model: names are checked via hash-set span lookup (zero allocation) + one merged regex;
/// full-path rules are evaluated only for directories (unless the user explicitly targets files),
/// so index-time cost is O(directories).
/// </summary>
public sealed class ExclusionMatcher
{
    private const RegexOptions Opts = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled;

    private readonly HashSet<string> _dirNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _fileNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string>.AlternateLookup<ReadOnlySpan<char>> _dirNamesLookup;
    private readonly HashSet<string>.AlternateLookup<ReadOnlySpan<char>> _fileNamesLookup;
    private readonly Regex? _dirNameRegex;
    private readonly Regex? _fileNameRegex;
    private readonly HashSet<string> _dirPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _filePaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly string[] _allPathRules;
    private readonly Regex? _dirFullRegex;
    private readonly Regex? _fileFullRegex;
    private readonly bool _excludeHidden;
    private readonly bool _excludeSystem;
    private readonly HashSet<string> _ruleKeys;

    public string Fingerprint { get; }
    public bool HasFullPathRules => _dirFullRegex != null || _fileFullRegex != null;
    public bool HasFileFullPathRules => _fileFullRegex != null || _filePaths.Count > 0;
    public bool ExcludeHidden => _excludeHidden;
    public bool ExcludeSystem => _excludeSystem;

    public static ExclusionMatcher Empty { get; } = new(new ExclusionSettings { Rules = new(), ExcludeHidden = false, ExcludeSystem = false });

    public static ExclusionMatcher Create(ExclusionSettings s) => new(s);

    private ExclusionMatcher(ExclusionSettings s)
    {
        _excludeHidden = s.ExcludeHidden;
        _excludeSystem = s.ExcludeSystem;
        _ruleKeys = new HashSet<string>(StringComparer.Ordinal);

        var dirNameWild = new List<string>();
        var fileNameWild = new List<string>();
        var dirFull = new List<string>();
        var fileFull = new List<string>();
        var allPaths = new List<string>();

        foreach (var r in s.Rules ?? new List<ExclusionRule>())
        {
            if (!r.Enabled || string.IsNullOrWhiteSpace(r.Pattern)) continue;
            if (r.Validate() != null) { Logger.Warn($"Skipping invalid exclusion rule {r}"); continue; }
            bool dirs = r.Target != ExclusionTarget.FilesOnly;
            bool files = r.Target != ExclusionTarget.DirectoriesOnly;
            _ruleKeys.Add(RuleKey(r));
            switch (r.Kind)
            {
                case ExclusionKind.Name:
                {
                    var p = r.Pattern.Trim();
                    if (WildcardPattern.HasWildcards(p))
                    {
                        var src = WildcardPattern.NameToRegexSource(p);
                        if (dirs) dirNameWild.Add(src);
                        if (files) fileNameWild.Add(src);
                    }
                    else
                    {
                        if (dirs) _dirNames.Add(p);
                        if (files) _fileNames.Add(p);
                    }
                    break;
                }
                case ExclusionKind.Path:
                {
                    var p = PathUtil.Normalize(r.Pattern);
                    if (p.Length == 0) break;
                    if (dirs) _dirPaths.Add(p);
                    if (files) _filePaths.Add(p);
                    allPaths.Add(p);
                    break;
                }
                case ExclusionKind.Glob:
                {
                    var src = WildcardPattern.GlobToRegexSource(r.Pattern);
                    if (dirs) dirFull.Add(src);
                    if (files) fileFull.Add(src);
                    break;
                }
                case ExclusionKind.Regex:
                    if (dirs) dirFull.Add(r.Pattern);
                    if (files) fileFull.Add(r.Pattern);
                    break;
            }
        }

        _dirNamesLookup = _dirNames.GetAlternateLookup<ReadOnlySpan<char>>();
        _fileNamesLookup = _fileNames.GetAlternateLookup<ReadOnlySpan<char>>();
        _dirNameRegex = Combine(dirNameWild, anchored: true);
        _fileNameRegex = Combine(fileNameWild, anchored: true);
        _dirFullRegex = Combine(dirFull, anchored: false);
        _fileFullRegex = Combine(fileFull, anchored: false);
        allPaths.Sort((a, b) => a.Length.CompareTo(b.Length));
        _allPathRules = allPaths.ToArray();
        Fingerprint = ComputeFingerprint();
    }

    private static Regex? Combine(List<string> sources, bool anchored)
    {
        if (sources.Count == 0) return null;
        var sb = new StringBuilder();
        if (anchored) sb.Append("^(?:");
        for (int i = 0; i < sources.Count; i++)
        {
            if (i > 0) sb.Append('|');
            sb.Append("(?:").Append(sources[i]).Append(')');
        }
        if (anchored) sb.Append(")$");
        try { return new Regex(sb.ToString(), Opts); }
        catch (ArgumentException ex)
        {
            Logger.Warn("Failed to compile combined exclusion regex", ex);
            return null;
        }
    }

    private static string RuleKey(ExclusionRule r)
    {
        var pat = r.Kind == ExclusionKind.Path ? PathUtil.Normalize(r.Pattern) : r.Pattern.Trim();
        if (r.Kind != ExclusionKind.Regex) pat = pat.ToLowerInvariant();
        return $"{r.Kind}|{r.Target}|{pat}";
    }

    private string ComputeFingerprint()
    {
        var keys = _ruleKeys.ToList();
        keys.Sort(StringComparer.Ordinal);
        var sb = new StringBuilder();
        foreach (var k in keys) sb.Append(k).Append('\n');
        sb.Append("hidden=").Append(_excludeHidden).Append(";system=").Append(_excludeSystem);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(hash, 0, 16);
    }

    /// <summary>
    /// ExcludeHidden: anything with Hidden. ExcludeSystem: "protected operating system files" (Hidden + System),
    /// same semantics as Explorer's option, so ordinary folders that merely carry System (customized folders) stay visible.
    /// </summary>
    private bool AttributesExcluded(FileAttributes attrs)
    {
        bool hidden = (attrs & FileAttributes.Hidden) != 0;
        if (_excludeHidden && hidden) return true;
        return _excludeSystem && hidden && (attrs & FileAttributes.System) != 0;
    }

    /// <summary>
    /// Index-time check of a single entry; ancestors are assumed to be already accepted by the caller.
    /// <paramref name="parentFullPath"/> is the full path of the containing directory (null when unknown).
    /// </summary>
    public bool IsExcludedEntry(ReadOnlySpan<char> name, string? parentFullPath, bool isDirectory, FileAttributes attrs)
    {
        if (attrs != 0 && AttributesExcluded(attrs)) return true;
        if (isDirectory)
        {
            if (_dirNames.Count > 0 && _dirNamesLookup.Contains(name)) return true;
            if (_dirNameRegex != null && _dirNameRegex.IsMatch(name)) return true;
            if ((_dirPaths.Count > 0 || _dirFullRegex != null) && parentFullPath != null)
            {
                var full = CombinePath(parentFullPath, name);
                if (_dirPaths.Contains(full)) return true;
                if (_dirFullRegex != null && _dirFullRegex.IsMatch(full)) return true;
            }
        }
        else
        {
            if (_fileNames.Count > 0 && _fileNamesLookup.Contains(name)) return true;
            if (_fileNameRegex != null && _fileNameRegex.IsMatch(name)) return true;
            if ((_filePaths.Count > 0 || _fileFullRegex != null) && parentFullPath != null)
            {
                var full = CombinePath(parentFullPath, name);
                if (_filePaths.Contains(full)) return true;
                if (_fileFullRegex != null && _fileFullRegex.IsMatch(full)) return true;
            }
        }
        return false;
    }

    /// <summary>General check for an arbitrary full path (all ancestor segments are checked).</summary>
    public bool IsExcludedPath(string fullPath, bool isDirectory, FileAttributes? attrs = null)
    {
        if (string.IsNullOrEmpty(fullPath) || !PathUtil.IsFileSystemPath(fullPath)) return false;
        if (attrs is { } a && a != 0 && fullPath.Length > 3 && AttributesExcluded(a)) return true;

        foreach (var p in _allPathRules)
            if (PathUtil.IsUnder(fullPath, p)) return true;

        var span = fullPath.AsSpan();
        // Skip root ("C:\" or "\\server\share\")
        int start = span.StartsWith(@"\\") ? IndexOfNth(span, '\\', 4) + 1 : 3;
        if (start <= 0 || start > span.Length) start = Math.Min(3, span.Length);
        while (start < span.Length)
        {
            int sep = span[start..].IndexOf('\\');
            bool last = sep < 0;
            var seg = last ? span[start..] : span.Slice(start, sep);
            if (seg.Length > 0)
            {
                bool segIsDir = !last || isDirectory;
                if (segIsDir)
                {
                    if (_dirNames.Count > 0 && _dirNamesLookup.Contains(seg)) return true;
                    if (_dirNameRegex != null && _dirNameRegex.IsMatch(seg)) return true;
                }
                else
                {
                    if (_fileNames.Count > 0 && _fileNamesLookup.Contains(seg)) return true;
                    if (_fileNameRegex != null && _fileNameRegex.IsMatch(seg)) return true;
                }
            }
            if (last) break;
            start += sep + 1;
        }

        if (_dirFullRegex != null)
        {
            // Directory regexes also match subtree suffixes; for files test the parent directory.
            var dirPart = isDirectory ? fullPath : PathUtil.GetParent(fullPath);
            if (dirPart != null && _dirFullRegex.IsMatch(dirPart)) return true;
        }
        if (!isDirectory && _fileFullRegex != null && _fileFullRegex.IsMatch(fullPath)) return true;
        return false;
    }

    private static int IndexOfNth(ReadOnlySpan<char> s, char c, int n)
    {
        int count = 0;
        for (int i = 0; i < s.Length; i++)
            if (s[i] == c && ++count == n) return i;
        return s.Length - 1;
    }

    private static string CombinePath(string parent, ReadOnlySpan<char> name) =>
        parent.EndsWith('\\') ? string.Concat(parent.AsSpan(), name) : string.Concat(parent.AsSpan(), "\\", name);

    public ExclusionDiff Diff(ExclusionMatcher previous)
    {
        bool additions = false, removals = false, removedNonPath = false;
        var removedPaths = new List<string>();
        foreach (var k in _ruleKeys)
            if (!previous._ruleKeys.Contains(k)) additions = true;
        foreach (var k in previous._ruleKeys)
        {
            if (_ruleKeys.Contains(k)) continue;
            removals = true;
            if (k.StartsWith("Path|", StringComparison.Ordinal))
                removedPaths.Add(previous._allPathRules.FirstOrDefault(p => k.EndsWith("|" + p.ToLowerInvariant(), StringComparison.Ordinal)) ?? k[(k.LastIndexOf('|') + 1)..]);
            else removedNonPath = true;
        }
        if (_excludeHidden && !previous._excludeHidden) additions = true;
        if (_excludeSystem && !previous._excludeSystem) additions = true;
        if (!_excludeHidden && previous._excludeHidden) { removals = true; removedNonPath = true; }
        if (!_excludeSystem && previous._excludeSystem) { removals = true; removedNonPath = true; }
        return new ExclusionDiff(additions, removals, removedPaths, removedNonPath);
    }
}

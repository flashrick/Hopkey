namespace Hopkey.Core.Exclusion;

public static class DefaultExclusions
{
    public static List<ExclusionRule> Create() => new()
    {
        new(ExclusionKind.Name, "$RECYCLE.BIN", ExclusionTarget.DirectoriesOnly, true),
        new(ExclusionKind.Name, "System Volume Information", ExclusionTarget.DirectoriesOnly, true),
        new(ExclusionKind.Name, "$WinREAgent", ExclusionTarget.DirectoriesOnly, true),
        new(ExclusionKind.Name, "node_modules", ExclusionTarget.DirectoriesOnly, true),
        new(ExclusionKind.Name, ".git", ExclusionTarget.DirectoriesOnly, true),
        new(ExclusionKind.Name, ".svn", ExclusionTarget.DirectoriesOnly, true),
        new(ExclusionKind.Name, ".hg", ExclusionTarget.DirectoriesOnly, true),
        new(ExclusionKind.Name, "__pycache__", ExclusionTarget.DirectoriesOnly, true),
        new(ExclusionKind.Name, ".vs", ExclusionTarget.DirectoriesOnly, true),
        new(ExclusionKind.Name, "*.tmp", ExclusionTarget.FilesOnly, true),
        new(ExclusionKind.Name, "~$*", ExclusionTarget.FilesOnly, true),
        new(ExclusionKind.Path, @"%SystemRoot%\WinSxS", ExclusionTarget.DirectoriesOnly, true),
        new(ExclusionKind.Path, @"%SystemRoot%\Installer", ExclusionTarget.DirectoriesOnly, true),
        new(ExclusionKind.Path, @"%SystemRoot%\servicing", ExclusionTarget.DirectoriesOnly, true),
        new(ExclusionKind.Path, @"%SystemRoot%\SoftwareDistribution", ExclusionTarget.DirectoriesOnly, true),
        new(ExclusionKind.Path, @"%TEMP%", ExclusionTarget.DirectoriesOnly, true),
        new(ExclusionKind.Path, @"%ProgramData%\Package Cache", ExclusionTarget.DirectoriesOnly, true),
    };

    /// <summary>Adds built-in rules that are missing (by kind+pattern) so upgrades get new defaults.</summary>
    public static void MergeMissingBuiltIns(List<ExclusionRule> rules)
    {
        foreach (var d in Create())
        {
            bool exists = rules.Exists(r => r.Kind == d.Kind && string.Equals(r.Pattern, d.Pattern, StringComparison.OrdinalIgnoreCase));
            if (!exists) rules.Add(d);
        }
    }
}

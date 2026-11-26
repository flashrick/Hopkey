namespace Hopkey.Core.Exclusion;

public enum ExclusionKind
{
    /// <summary>Absolute folder path (may contain %ENV%); excludes the folder and its subtree.</summary>
    Path,
    /// <summary>File/folder name or wildcard name: node_modules, .git, *.tmp</summary>
    Name,
    /// <summary>Wildcard over full path: **\build\obj, C:\Users\*\AppData\Local\Temp</summary>
    Glob,
    /// <summary>.NET regex matched against the full path.</summary>
    Regex,
}

public enum ExclusionTarget { Both, DirectoriesOnly, FilesOnly }

public sealed class ExclusionRule
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public ExclusionKind Kind { get; set; }
    public string Pattern { get; set; } = "";
    public ExclusionTarget Target { get; set; } = ExclusionTarget.Both;
    public bool Enabled { get; set; } = true;
    public bool BuiltIn { get; set; }

    public ExclusionRule() { }

    public ExclusionRule(ExclusionKind kind, string pattern, ExclusionTarget target = ExclusionTarget.Both, bool builtIn = false)
    {
        Kind = kind;
        Pattern = pattern;
        Target = target;
        BuiltIn = builtIn;
    }

    public ExclusionRule Copy() => new()
    {
        Id = Id, Kind = Kind, Pattern = Pattern, Target = Target, Enabled = Enabled, BuiltIn = BuiltIn,
    };

    /// <summary>Validate the rule. Returns null if valid, otherwise an error message key.</summary>
    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(Pattern)) return "Pattern is empty";
        switch (Kind)
        {
            case ExclusionKind.Path:
                var expanded = Environment.ExpandEnvironmentVariables(Pattern);
                if (!System.IO.Path.IsPathFullyQualified(expanded)) return "Path must be absolute";
                break;
            case ExclusionKind.Name:
                if (Pattern.IndexOfAny(['\\', '/', ':']) >= 0) return "Name pattern must not contain \\ / :";
                break;
            case ExclusionKind.Regex:
                try { _ = new System.Text.RegularExpressions.Regex(Pattern); }
                catch (ArgumentException ex) { return ex.Message; }
                break;
            case ExclusionKind.Glob:
                break;
        }
        return null;
    }

    public override string ToString() => $"{Kind}:{Pattern}({Target}{(Enabled ? "" : ",off")})";
}

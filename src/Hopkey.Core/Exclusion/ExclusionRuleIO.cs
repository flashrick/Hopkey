using System.Text.Json;
using Hopkey.Core.Config;

namespace Hopkey.Core.Exclusion;

/// <summary>Import/export exclusion rules: JSON array, or plain text one rule per line.</summary>
public static class ExclusionRuleIO
{
    public static void Export(string file, IEnumerable<ExclusionRule> rules)
    {
        var list = rules.Select(r => r.Copy()).ToList();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(list, JsonContext.Default.ListExclusionRule);
        File.WriteAllBytes(file, bytes);
    }

    /// <summary>
    /// Plain text format: one per line. "path:C:\x", "name:node_modules", "glob:**\obj", "regex:...".
    /// Lines without prefix: absolute path → Path, contains '\' → Glob, otherwise Name. '#' starts a comment.
    /// </summary>
    public static List<ExclusionRule> Import(string file)
    {
        var text = File.ReadAllText(file).Trim();
        if (text.StartsWith('['))
        {
            var list = JsonSerializer.Deserialize(text, JsonContext.Default.ListExclusionRule) ?? new();
            foreach (var r in list) { r.Id = Guid.NewGuid(); r.BuiltIn = false; }
            return list.Where(r => r.Validate() == null).ToList();
        }
        var result = new List<ExclusionRule>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var rule = ParseLine(line);
            if (rule != null && rule.Validate() == null) result.Add(rule);
        }
        return result;
    }

    public static ExclusionRule? ParseLine(string line)
    {
        static ExclusionRule Make(ExclusionKind k, string p) => new(k, p.Trim(), k == ExclusionKind.Name && p.Contains('.') && p.StartsWith('*') ? ExclusionTarget.FilesOnly : ExclusionTarget.Both);
        int colon = line.IndexOf(':');
        if (colon > 1)
        {
            var prefix = line[..colon].ToLowerInvariant();
            var rest = line[(colon + 1)..];
            switch (prefix)
            {
                case "path": return Make(ExclusionKind.Path, rest);
                case "name": return Make(ExclusionKind.Name, rest);
                case "glob": return Make(ExclusionKind.Glob, rest);
                case "regex": return Make(ExclusionKind.Regex, rest);
            }
        }
        var expanded = Environment.ExpandEnvironmentVariables(line);
        if (Path.IsPathFullyQualified(expanded) && !WildcardPattern.HasWildcards(line)) return Make(ExclusionKind.Path, line);
        if (line.Contains('\\') || line.Contains('/')) return Make(ExclusionKind.Glob, line);
        return Make(ExclusionKind.Name, line);
    }
}

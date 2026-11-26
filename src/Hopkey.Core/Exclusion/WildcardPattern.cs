using System.Text;

namespace Hopkey.Core.Exclusion;

/// <summary>Converts wildcard patterns into regex sources. Pure functions.</summary>
public static class WildcardPattern
{
    public static bool HasWildcards(string s) => s.AsSpan().IndexOfAny('*', '?') >= 0;

    /// <summary>Name wildcard (no separators): '*' → '.*', '?' → '.'</summary>
    public static string NameToRegexSource(string pattern)
    {
        var sb = new StringBuilder(pattern.Length * 2);
        foreach (char c in pattern)
        {
            switch (c)
            {
                case '*': sb.Append(".*"); break;
                case '?': sb.Append('.'); break;
                default: sb.Append(System.Text.RegularExpressions.Regex.Escape(c.ToString())); break;
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Full-path glob: "**" matches across separators, "*" within one segment, "?" one char.
    /// A pattern starting with "**\" matches anywhere. The match is anchored and also matches subtrees ("(\\.*)?$").
    /// </summary>
    public static string GlobToRegexSource(string pattern)
    {
        var p = Environment.ExpandEnvironmentVariables(pattern.Trim()).Replace('/', '\\').TrimEnd('\\');
        var sb = new StringBuilder(p.Length * 2 + 8);
        sb.Append('^');
        for (int i = 0; i < p.Length; i++)
        {
            char c = p[i];
            if (c == '*')
            {
                if (i + 1 < p.Length && p[i + 1] == '*')
                {
                    i++;
                    // "**\" → any number of segments (including none)
                    if (i + 1 < p.Length && p[i + 1] == '\\') { i++; sb.Append(@"(?:.*\\)?"); }
                    else sb.Append(".*");
                }
                else sb.Append(@"[^\\]*");
            }
            else if (c == '?') sb.Append(@"[^\\]");
            else sb.Append(System.Text.RegularExpressions.Regex.Escape(c.ToString()));
        }
        sb.Append(@"(?:\\.*)?$");
        return sb.ToString();
    }
}

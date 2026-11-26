using Hopkey.Core.Config;

namespace Hopkey.Core.Search;

/// <summary>Text → SearchQuery. Pure function.</summary>
public static class QueryParser
{
    public static SearchQuery Parse(string? text, IReadOnlyList<WebSearchEngine>? webEngines)
    {
        text ??= string.Empty;
        var trimmed = text.Trim();
        var keywords = new List<string>();
        var type = TypeFilter.Any;
        string[]? exts = null;
        WebSearchEngine? web = null;
        string? webText = null;
        string? expr = null;

        if (trimmed.Length == 0)
            return new SearchQuery(text, keywords, type, null, null, null, null) { Normalized = "" };

        // Calculator
        if (trimmed.StartsWith('='))
        {
            var e = trimmed[1..].Trim();
            if (e.Length > 0) expr = e;
        }
        else if (Calculator.LooksLikeExpression(trimmed))
        {
            expr = trimmed;
        }

        var tokens = trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

        // Web search: "<keyword> <text>"
        if (webEngines != null && tokens.Length >= 2)
        {
            var first = tokens[0];
            foreach (var w in webEngines)
            {
                if (w.Enabled && string.Equals(w.Keyword, first, StringComparison.OrdinalIgnoreCase))
                {
                    web = w;
                    webText = trimmed[first.Length..].Trim();
                    break;
                }
            }
        }

        for (int i = 0; i < tokens.Length; i++)
        {
            var t = tokens[i];
            if (web != null && i == 0) continue;
            int colon = t.IndexOf(':');
            if (colon > 0 && !(colon == 1 && char.IsAsciiLetter(t[0]) && t.Length > 2 && t[2] == '\\')) // keep "C:\..." as keyword
            {
                var prefix = t[..colon].ToLowerInvariant();
                var rest = t[(colon + 1)..];
                bool handled = true;
                switch (prefix)
                {
                    case "folder": case "dir": type = TypeFilter.Folder; break;
                    case "file": type = TypeFilter.File; break;
                    case "app": type = TypeFilter.App; break;
                    case "ext":
                        exts = rest.Split(',', ';', '|').Where(x => x.Length > 0)
                            .Select(x => "." + x.TrimStart('.').ToLowerInvariant()).Distinct().ToArray();
                        if (exts.Length == 0) exts = null;
                        rest = "";
                        break;
                    default: handled = false; break;
                }
                if (handled)
                {
                    if (rest.Length > 0) keywords.Add(Lower(rest));
                    continue;
                }
            }
            keywords.Add(Lower(t));
        }

        return new SearchQuery(text, keywords, type, exts, web, webText, expr) { Normalized = Lower(trimmed) };
    }

    /// <summary>Same lowering rule as the index's Lower arena.</summary>
    public static string Lower(string s)
    {
        return string.Create(s.Length, s, static (dst, src) => Indexing.NameArena.LowerInto(src, dst));
    }
}

namespace Hopkey.Core.Search;

/// <summary>
/// Abbreviation search: "ppt" → "PowerPoint", "vscode" → "Visual Studio Code", "xls" → "Excel".
/// Two mechanisms:
/// 1. Word-start abbreviation: the keyword is a subsequence of the name that starts at a word start and
///    touches at least two word starts (P·ower·P·oin·t). Scored by DP so word starts / contiguous runs win.
/// 2. A small table of well-known abbreviations that are not letter subsequences of the name (xls → Excel).
/// Only used for small sets (apps, favorites, history), never for the file index.
/// </summary>
public static class Abbreviation
{
    // Prefix tier: an app abbreviation is a strong intent signal. With the app bonus (+200) it must beat
    // file-index hits such as "ppt….pptx" (prefix 800 + up to ~80 length/depth bonus) and "a.pptx" (~670).
    public const double Base = 650;   // all chars on word starts → Base + Range = Prefix
    public const double Range = 150;
    public const double KnownScore = NameMatcher.Prefix + 50;

    private const int BoundaryBonus = 10, ConsecutiveBonus = 6, GapBonus = 1;

    /// <summary>Well-known abbreviation → the word(s) that must appear as a whole word in the name.</summary>
    private static readonly Dictionary<string, string[]> s_known = new(StringComparer.Ordinal)
    {
        ["ppt"] = new[] { "powerpoint" },
        ["pptx"] = new[] { "powerpoint" },
        ["xls"] = new[] { "excel" },
        ["xlsx"] = new[] { "excel" },
        ["doc"] = new[] { "word" },
        ["docx"] = new[] { "word" },
        ["ps"] = new[] { "photoshop" },
        ["ai"] = new[] { "illustrator" },
        ["pr"] = new[] { "premiere" },
        ["ae"] = new[] { "after effects" },
        ["lr"] = new[] { "lightroom" },
        ["vs"] = new[] { "visual studio" },
        ["cmd"] = new[] { "command prompt" },
        ["calc"] = new[] { "calculator" },
        ["ie"] = new[] { "internet explorer" },
        ["regedit"] = new[] { "registry editor" },
        ["taskmgr"] = new[] { "task manager" },
        ["mspaint"] = new[] { "paint" },
    };

    /// <summary>Score for a well-known abbreviation whose expansion appears as a whole word in the name; -1 otherwise.</summary>
    public static double ScoreKnown(string lower, string original, string kw, List<(int, int)>? highlights)
    {
        if (!s_known.TryGetValue(kw, out var expansions)) return -1;
        foreach (var exp in expansions)
        {
            int from = 0;
            while (from <= lower.Length - exp.Length)
            {
                int idx = lower.IndexOf(exp, from, StringComparison.Ordinal);
                if (idx < 0) break;
                int end = idx + exp.Length;
                // whole word: "Microsoft Word" yes, "WordPad" no
                if (NameMatcher.IsBoundary(original, idx) && (end == lower.Length || !char.IsLetter(lower[end])))
                {
                    highlights?.Add((idx, exp.Length));
                    return KnownScore - Math.Min(idx, 40);
                }
                from = idx + 1;
            }
        }
        return -1;
    }

    /// <summary>Word-start abbreviation score in [Base, Base+Range], or -1 if the keyword is not such an abbreviation.</summary>
    public static double ScoreWordStarts(string original, string lower, string kw, List<(int, int)>? highlights)
    {
        int m = kw.Length, n = lower.Length;
        if (m < 2 || m > n || n != original.Length) return -1;

        var boundary = new bool[n];
        for (int i = 0; i < n; i++) boundary[i] = char.IsLetterOrDigit(lower[i]) && NameMatcher.IsBoundary(original, i);

        // dp[k*n+i]: best score with kw[k] matched at i (int.MinValue = impossible); parent for backtracking.
        var dp = new int[m * n];
        var parent = new int[m * n];
        Array.Fill(dp, int.MinValue);
        for (int i = 0; i < n; i++)
            if (boundary[i] && lower[i] == kw[0]) dp[i] = BoundaryBonus;

        for (int k = 1; k < m; k++)
        {
            int prevRow = (k - 1) * n, row = k * n;
            int bestFar = int.MinValue, bestFarAt = -1; // max of dp[k-1][j] for j < i-1
            for (int i = 1; i < n; i++)
            {
                if (i >= 2 && dp[prevRow + i - 2] > bestFar) { bestFar = dp[prevRow + i - 2]; bestFarAt = i - 2; }
                if (lower[i] != kw[k]) continue;
                int adj = dp[prevRow + i - 1];
                int viaAdj = adj == int.MinValue ? int.MinValue : adj + (boundary[i] ? BoundaryBonus : ConsecutiveBonus);
                int viaFar = bestFar == int.MinValue ? int.MinValue : bestFar + (boundary[i] ? BoundaryBonus : GapBonus);
                if (viaAdj == int.MinValue && viaFar == int.MinValue) continue;
                if (viaAdj >= viaFar) { dp[row + i] = viaAdj; parent[row + i] = i - 1; }
                else { dp[row + i] = viaFar; parent[row + i] = bestFarAt; }
            }
        }

        int lastRow = (m - 1) * n, best = int.MinValue, at = -1;
        for (int i = 0; i < n; i++) if (dp[lastRow + i] > best) { best = dp[lastRow + i]; at = i; }
        if (at < 0) return -1;

        var pos = new int[m];
        for (int k = m - 1; k >= 0; k--) { pos[k] = at; if (k > 0) at = parent[k * n + at]; }
        int words = 0;
        foreach (var p in pos) if (boundary[p]) words++;
        if (words < 2) return -1; // within a single word it's just fuzzy, not an abbreviation

        if (highlights != null)
        {
            int runStart = pos[0];
            for (int k = 1; k <= m; k++)
                if (k == m || pos[k] != pos[k - 1] + 1) { highlights.Add((runStart, pos[k - 1] - runStart + 1)); if (k < m) runStart = pos[k]; }
        }
        return Base + Range * best / (double)(BoundaryBonus * m) - Math.Min(pos[0], 20);
    }
}

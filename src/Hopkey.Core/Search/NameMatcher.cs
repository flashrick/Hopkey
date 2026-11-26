namespace Hopkey.Core.Search;

/// <summary>Scoring of a single keyword against a name. Pure, allocation-free except for highlight output.</summary>
public static class NameMatcher
{
    public const double Exact = 1000;
    public const double Prefix = 800;
    public const double WordBoundary = 600;
    public const double Acronym = 500;
    public const double Substring = 300;
    public const double PathHit = 150;
    public const double PinyinFactor = 0.9;

    public static bool IsBoundary(ReadOnlySpan<char> original, int pos)
    {
        if (pos <= 0) return true;
        char prev = original[pos - 1], c = original[pos];
        if (prev is ' ' or '_' or '-' or '.' or '(' or '[' or '{' or '&' or '+' or ',') return true;
        if (char.IsLower(prev) && char.IsUpper(c)) return true;
        if (char.IsDigit(prev) != char.IsDigit(c) && char.IsLetterOrDigit(prev) && char.IsLetterOrDigit(c)) return true;
        if (Pinyin.PinyinHelper.IsCjk(prev) != Pinyin.PinyinHelper.IsCjk(c)) return true;
        return false;
    }

    /// <summary>Score when the keyword occurs at <paramref name="pos"/> of the lowercased name.</summary>
    public static double ScoreAt(ReadOnlySpan<char> original, int nameLen, int kwLen, int pos)
    {
        if (pos == 0) return kwLen == nameLen ? Exact : Prefix;
        if (IsBoundary(original, pos)) return WordBoundary - pos;
        return Math.Max(50, Substring - pos * 2);
    }

    /// <summary>Best substring score of <paramref name="kw"/> within lowercased name; -1 if absent.</summary>
    public static double ScoreSubstring(ReadOnlySpan<char> lowerName, ReadOnlySpan<char> original, ReadOnlySpan<char> kw, out int bestPos)
    {
        bestPos = -1;
        if (kw.Length == 0 || kw.Length > lowerName.Length) return -1;
        double best = -1;
        int from = 0;
        while (from <= lowerName.Length - kw.Length)
        {
            int idx = lowerName[from..].IndexOf(kw);
            if (idx < 0) break;
            int pos = from + idx;
            double s = ScoreAt(original, lowerName.Length, kw.Length, pos);
            if (s > best) { best = s; bestPos = pos; }
            if (pos == 0 || best >= WordBoundary - pos) break; // can't do better later
            from = pos + 1;
        }
        return best;
    }

    /// <summary>Acronym match: "vsc" vs acronym "vsc…". Returns score or -1.</summary>
    public static double ScoreAcronym(string acronym, ReadOnlySpan<char> kw)
    {
        if (kw.Length < 2 || acronym.Length < kw.Length) return -1;
        var a = acronym.AsSpan();
        if (a.StartsWith(kw)) return Acronym + (a.Length == kw.Length ? 50 : 0);
        if (a.IndexOf(kw) >= 0) return Acronym - 100;
        return -1;
    }

    /// <summary>fzf-like subsequence match for small sets: 100 − gaps×5 (min 1); -1 if not a subsequence.</summary>
    public static double ScoreFuzzy(ReadOnlySpan<char> lowerName, ReadOnlySpan<char> kw, List<(int, int)>? highlights)
    {
        if (kw.Length == 0 || kw.Length > lowerName.Length) return -1;
        int ki = 0, gaps = 0, last = -1, runStart = -1;
        for (int i = 0; i < lowerName.Length && ki < kw.Length; i++)
        {
            if (lowerName[i] == kw[ki])
            {
                if (last >= 0 && i != last + 1) { gaps += i - last - 1; if (runStart >= 0) highlights?.Add((runStart, last - runStart + 1)); runStart = i; }
                else if (runStart < 0) runStart = i;
                last = i;
                ki++;
            }
        }
        if (ki < kw.Length) { highlights?.Clear(); return -1; }
        if (runStart >= 0) highlights?.Add((runStart, last - runStart + 1));
        return Math.Max(1, 100 - gaps * 5);
    }

    /// <summary>
    /// Full scoring of several keywords against a small-set item (app/history/favorite).
    /// Every keyword must match name, initials (pinyin), full pinyin, acronym, or fuzzy subsequence.
    /// </summary>
    public static double ScoreItem(string name, string lower, string initials, string? fullPinyin, string acronym,
        IReadOnlyList<string> keywords, List<(int, int)> highlights, bool allowFuzzy = true)
    {
        double total = 0;
        foreach (var kw in keywords)
        {
            double s = ScoreSubstring(lower, name, kw, out int pos);
            if (s >= 0) { highlights.Add((pos, kw.Length)); total += s; continue; }
            if (!ReferenceEquals(initials, lower) && initials != lower)
            {
                s = ScoreSubstring(initials, name, kw, out pos);
                if (s >= 0) { highlights.Add((pos, kw.Length)); total += s * PinyinFactor; continue; }
            }
            if (fullPinyin != null && fullPinyin.Contains(kw, StringComparison.Ordinal))
            {
                total += (fullPinyin.StartsWith(kw, StringComparison.Ordinal) ? Prefix : Substring) * PinyinFactor;
                continue;
            }
            s = ScoreAcronym(acronym, kw);
            if (s >= 0) { total += s; continue; }
            if (allowFuzzy)
            {
                var tmp = new List<(int, int)>();
                s = ScoreFuzzy(lower, kw, tmp);
                if (s >= 0) { highlights.AddRange(tmp); total += s; continue; }
            }
            return -1;
        }
        return total;
    }
}

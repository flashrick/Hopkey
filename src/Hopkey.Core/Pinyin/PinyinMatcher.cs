namespace Hopkey.Core.Pinyin;

/// <summary>
/// Full / mixed pinyin matching of an ASCII keyword against a name: the keyword must match a contiguous run of
/// name chars where each CJK char consumes a non-empty prefix of one of its readings (so "baogao", "baog", "bg"
/// and "bgao" all match 报告; "yinhang" matches 银行) and every other char matches itself case-insensitively.
/// </summary>
public static class PinyinMatcher
{
    /// <summary>Keywords worth full-pinyin matching (ASCII letters/digits, ≥ 2 chars, table loaded).</summary>
    public static bool IsFullPinyinQuery(ReadOnlySpan<char> kw) =>
        kw.Length >= 2 && PinyinInitialsTable.HasFullPinyin && PinyinHelper.IsPinyinQuery(kw) && kw.IndexOf(' ') < 0;

    /// <summary>First position in <paramref name="name"/> where <paramref name="kw"/> matches; len = name chars covered.</summary>
    public static bool Find(ReadOnlySpan<char> name, ReadOnlySpan<char> kw, out int pos, out int len)
    {
        pos = len = 0;
        if (kw.Length == 0 || !PinyinHelper.ContainsCjk(name)) return false;
        int states = (name.Length + 1) * (kw.Length + 1);
        Span<bool> failed = states <= 2048 ? stackalloc bool[states] : new bool[states];
        failed.Clear();
        char k0 = kw[0];
        for (int s = 0; s < name.Length; s++)
        {
            if (!CanStart(name[s], k0)) continue;
            if (Match(name, kw, s, 0, failed, out int end)) { pos = s; len = end - s; return true; }
        }
        return false;
    }

    /// <summary>Does <paramref name="kw"/> match starting exactly at <paramref name="start"/>?</summary>
    public static bool MatchAt(ReadOnlySpan<char> name, int start, ReadOnlySpan<char> kw, out int len)
    {
        len = 0;
        if (kw.Length == 0 || (uint)start >= (uint)name.Length || !CanStart(name[start], kw[0])) return false;
        int states = (name.Length + 1) * (kw.Length + 1);
        Span<bool> failed = states <= 2048 ? stackalloc bool[states] : new bool[states];
        failed.Clear();
        if (!Match(name, kw, start, 0, failed, out int end)) return false;
        len = end - start;
        return true;
    }

    private static bool CanStart(char c, char k0)
    {
        if (PinyinHelper.IsCjk(c))
        {
            var r = PinyinInitialsTable.GetReadings(c);
            if (r == null) return false;
            foreach (var s in r) if (s[0] == k0) return true;
            return false;
        }
        return char.ToLowerInvariant(c) == k0;
    }

    private static bool Match(ReadOnlySpan<char> name, ReadOnlySpan<char> kw, int ni, int ki, Span<bool> failed, out int end)
    {
        end = ni;
        if (ki == kw.Length) return true;
        if (ni == name.Length) return false;
        int state = ni * (kw.Length + 1) + ki;
        if (failed[state]) return false;
        char c = name[ni];
        if (PinyinHelper.IsCjk(c))
        {
            var readings = PinyinInitialsTable.GetReadings(c);
            if (readings != null)
            {
                var rest = kw[ki..];
                foreach (var r in readings)
                {
                    // Longest prefix first: full syllables are the common case.
                    int max = Math.Min(r.Length, rest.Length), common = 0;
                    while (common < max && r[common] == rest[common]) common++;
                    for (int l = common; l >= 1; l--)
                        if (Match(name, kw, ni + 1, ki + l, failed, out end)) return true;
                }
            }
        }
        else if (char.ToLowerInvariant(c) == kw[ki] && Match(name, kw, ni + 1, ki + 1, failed, out end)) return true;
        failed[state] = true;
        return false;
    }

    /// <summary>
    /// Initials strings to pre-filter the index's pinyin-initials arena for a full-pinyin keyword: one per
    /// segmentation of <paramref name="kw"/> into syllables (the last may be a syllable prefix; digits stay literal).
    /// "baogao" → "bg"; "xian" → "x", "xa", "xn" (…). Polyphones add the initials the index stores for them
    /// ("yinhang" → "yh", "yx": 行 is stored under x). Excludes <paramref name="kw"/> itself (scanned anyway). Max 32.
    /// </summary>
    public static List<string> Needles(string kw)
    {
        const int Max = 32;
        var result = new List<string>();
        if (!IsFullPinyinQuery(kw)) return result;
        var syl = PinyinInitialsTable.Syllables;
        var buf = new char[kw.Length];
        void Walk(int i, int n)
        {
            if (result.Count >= Max) return;
            if (i == kw.Length)
            {
                var s = new string(buf, 0, n);
                if (s != kw && !result.Contains(s)) result.Add(s);
                return;
            }
            if (char.IsAsciiDigit(kw[i])) { buf[n] = kw[i]; Walk(i + 1, n + 1); return; }
            for (int l = Math.Min(6, kw.Length - i); l >= 1; l--)
            {
                var tok = kw.Substring(i, l);
                string? inits = syl.Contains(tok) ? PinyinInitialsTable.ArenaInitials(tok)
                    : i + l == kw.Length ? PrefixInitials(tok, syl) : null;
                if (inits == null) continue;
                foreach (var c in inits) { buf[n] = c; Walk(i + l, n + 1); }
            }
        }
        Walk(0, 0);
        return result;
    }

    /// <summary>Union of arena initials of all syllables extending <paramref name="tok"/>; null if none.</summary>
    private static string? PrefixInitials(string tok, IReadOnlySet<string> syl)
    {
        string? acc = null;
        foreach (var s in syl)
        {
            if (s.Length <= tok.Length || !s.StartsWith(tok, StringComparison.Ordinal)) continue;
            acc ??= tok[..1];
            foreach (var c in PinyinInitialsTable.ArenaInitials(s)) if (acc.IndexOf(c) < 0) acc += c;
        }
        return acc;
    }
}

using System.Text;

namespace Hopkey.Core.Pinyin;

public static class PinyinHelper
{
    public static bool IsCjk(char c) => c is >= PinyinInitialsTable.First and <= PinyinInitialsTable.Last;

    public static bool ContainsCjk(ReadOnlySpan<char> s)
    {
        foreach (var c in s)
            if (IsCjk(c)) return true;
        return false;
    }

    /// <summary>
    /// Writes the lowercase "initials string" for <paramref name="name"/> into <paramref name="dest"/> (same length as name):
    /// CJK chars → pinyin initial (unknown → the char itself), others → lowercase char. Allows mixed matching ("bg报告").
    /// </summary>
    public static void WriteInitials(ReadOnlySpan<char> name, Span<char> dest)
    {
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            if (IsCjk(c))
            {
                byte b = PinyinInitialsTable.GetInitial(c);
                dest[i] = b != 0 ? (char)b : c;
            }
            else dest[i] = char.ToLowerInvariant(c);
        }
    }

    public static string GetInitials(string name)
    {
        if (!ContainsCjk(name)) return name.ToLowerInvariant();
        Span<char> buf = name.Length <= 256 ? stackalloc char[name.Length] : new char[name.Length];
        WriteInitials(name, buf);
        return new string(buf);
    }

    /// <summary>Full pinyin (no separators) when IME data is available; otherwise null.</summary>
    public static string? GetFullPinyin(string name)
    {
        if (!PinyinInitialsTable.HasFullPinyin || !ContainsCjk(name)) return null;
        var sb = new StringBuilder(name.Length * 4);
        foreach (var c in name)
        {
            if (IsCjk(c))
            {
                var f = PinyinInitialsTable.GetFull(c);
                sb.Append(f ?? c.ToString());
            }
            else sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }

    /// <summary>Is the query a candidate for pinyin matching (ASCII letters/digits only, at least 1 letter)?</summary>
    public static bool IsPinyinQuery(ReadOnlySpan<char> q)
    {
        bool letter = false;
        foreach (var c in q)
        {
            if (char.IsAsciiLetter(c)) letter = true;
            else if (!char.IsAsciiDigit(c) && c != ' ') return false;
        }
        return letter;
    }
}

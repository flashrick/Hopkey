using System.Runtime.InteropServices;
using System.Text;
using Hopkey.Core.Config;
using Hopkey.Core.Util;

namespace Hopkey.Core.Pinyin;

/// <summary>
/// U+4E00–U+9FFF → pinyin initial ('a'..'z', 0 = unknown), plus optional full pinyin per char.
/// Built in two stages:
///  1) instantly from GB2312 level-1 boundaries (3755 most common chars, sorted by pinyin in GB2312);
///  2) in background, enriched via the Windows Chinese IME (IFELanguage "MSIME.China") when available,
///     which covers level-2 / GBK chars and provides full pinyin. Result cached in %LocalAppData%.
/// </summary>
public static class PinyinInitialsTable
{
    public const char First = '一';
    public const char Last = '鿿';
    public const int Size = Last - First + 1; // 20992

    private static readonly byte[] s_initials = new byte[Size];
    private static string?[]? s_full;
    private static bool s_imeTried;
    private static volatile bool s_ready;
    private static readonly object s_gate = new();

    public static bool IsReady => s_ready;
    public static bool HasFullPinyin => s_full != null;

    /// <summary>Version bumps whenever the table content changes (callers may rebuild derived data).</summary>
    public static int Version { get; private set; }

    public static event Action? Updated;

    // GB2312 level-1 boundaries (code values) for each initial letter.
    private static readonly (int Code, char Letter)[] s_bounds =
    {
        (0xB0A1, 'a'), (0xB0C5, 'b'), (0xB2C1, 'c'), (0xB4EE, 'd'), (0xB6EA, 'e'), (0xB7A2, 'f'), (0xB8C1, 'g'),
        (0xB9FE, 'h'), (0xBBF7, 'j'), (0xBFA6, 'k'), (0xC0AC, 'l'), (0xC2E8, 'm'), (0xC4C3, 'n'), (0xC5B6, 'o'),
        (0xC5BE, 'p'), (0xC6DA, 'q'), (0xC8BB, 'r'), (0xC8F6, 's'), (0xCBFA, 't'), (0xCDDA, 'w'), (0xCEF4, 'x'),
        (0xD1B9, 'y'), (0xD4D1, 'z'),
    };
    private const int Level1End = 0xD7F9;

    public static void Initialize()
    {
        lock (s_gate)
        {
            if (s_ready) return;
            if (!TryLoadCache())
                BuildFromGb2312();
            s_ready = true;
            Version++;
        }
        // Enrich in background if cache didn't already include IME data.
        if (s_full == null && !s_imeTried)
        {
            var t = new Thread(EnrichFromIme) { IsBackground = true, Name = "CL-Pinyin", Priority = ThreadPriority.Lowest };
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
        }
    }

    public static byte GetInitial(char c) => c is >= First and <= Last ? s_initials[c - First] : (byte)0;

    public static string? GetFull(char c)
    {
        var f = s_full;
        return f != null && c is >= First and <= Last ? f[c - First] : null;
    }

    private static void BuildFromGb2312()
    {
        Encoding gb;
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            gb = Encoding.GetEncoding(936);
        }
        catch (Exception ex)
        {
            Logger.Warn("GB2312 encoding unavailable; pinyin initials disabled", ex);
            return;
        }
        Span<char> one = stackalloc char[1];
        Span<byte> bytes = stackalloc byte[4];
        for (int i = 0; i < Size; i++)
        {
            one[0] = (char)(First + i);
            int n = gb.GetBytes(one, bytes);
            if (n != 2) continue;
            int code = (bytes[0] << 8) | bytes[1];
            if (code < s_bounds[0].Code || code > Level1End) continue;
            byte letter = 0;
            for (int b = s_bounds.Length - 1; b >= 0; b--)
            {
                if (code >= s_bounds[b].Code) { letter = (byte)s_bounds[b].Letter; break; }
            }
            s_initials[i] = letter;
        }
    }

    // ---------------- Cache ----------------
    private const uint Magic = 0x59504C43; // "CLPY"

    private static bool TryLoadCache()
    {
        try
        {
            var file = AppPaths.PinyinCacheFile;
            if (!File.Exists(file)) return false;
            using var br = new BinaryReader(File.OpenRead(file), Encoding.UTF8);
            if (br.ReadUInt32() != Magic || br.ReadInt32() != 2) return false;
            var data = br.ReadBytes(Size);
            if (data.Length != Size) return false;
            data.CopyTo(s_initials, 0);
            bool hasFull = br.ReadBoolean();
            if (hasFull)
            {
                var full = new string?[Size];
                int count = br.ReadInt32();
                for (int k = 0; k < count; k++)
                {
                    int idx = br.ReadInt32();
                    var s = br.ReadString();
                    if ((uint)idx < Size) full[idx] = s;
                }
                s_full = full;
            }
            s_imeTried = br.ReadBoolean();
            return true;
        }
        catch (Exception ex)
        {
            Logger.Warn("Failed to load pinyin cache", ex);
            return false;
        }
    }

    private static void SaveCache()
    {
        try
        {
            using var ms = new MemoryStream();
            using (var bw = new BinaryWriter(ms, Encoding.UTF8, true))
            {
                bw.Write(Magic);
                bw.Write(2);
                bw.Write(s_initials);
                var full = s_full;
                bw.Write(full != null);
                if (full != null)
                {
                    int count = full.Count(f => f != null);
                    bw.Write(count);
                    for (int i = 0; i < full.Length; i++)
                        if (full[i] != null) { bw.Write(i); bw.Write(full[i]!); }
                }
                bw.Write(s_imeTried);
            }
            AppPaths.AtomicWrite(AppPaths.PinyinCacheFile, ms.ToArray());
        }
        catch (Exception ex) { Logger.Warn("Failed to save pinyin cache", ex); }
    }

    // ---------------- IME enrichment ----------------
    [ComImport, Guid("019F7152-E6DB-11d0-83C3-00C04FDDB82E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFELanguage
    {
        [PreserveSig] int Open();
        [PreserveSig] int Close();
        [PreserveSig] int GetJMorphResult(uint dwRequest, uint dwCMode, int cwchInput, [MarshalAs(UnmanagedType.LPWStr)] string pwchInput, IntPtr pfCInfo, out IntPtr ppResult);
        [PreserveSig] int GetConversionModeCaps(out uint pdwCaps);
        [PreserveSig] int GetPhonetic([MarshalAs(UnmanagedType.BStr)] string str, int start, int length, [MarshalAs(UnmanagedType.BStr)] out string? result);
        [PreserveSig] int GetConversion([MarshalAs(UnmanagedType.BStr)] string str, int start, int length, [MarshalAs(UnmanagedType.BStr)] out string? result);
    }

    private static void EnrichFromIme()
    {
        object? obj = null;
        try
        {
            var type = Type.GetTypeFromProgID("MSIME.China");
            s_imeTried = true;
            if (type == null) { Logger.Info("MSIME.China not available; pinyin limited to GB2312 level-1"); SaveCache(); return; }
            obj = Activator.CreateInstance(type);
            if (obj is not IFELanguage ife) return;
            if (ife.Open() != 0) return;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var full = new string?[Size];
            var initials = (byte[])s_initials.Clone();
            int filled = 0;
            for (int i = 0; i < Size; i++)
            {
                string ch = ((char)(First + i)).ToString();
                if (ife.GetPhonetic(ch, 1, -1, out var py) != 0 || string.IsNullOrEmpty(py)) continue;
                var letters = new StringBuilder(py.Length);
                foreach (var c in py)
                {
                    if (char.IsAsciiLetter(c)) letters.Append(char.ToLowerInvariant(c));
                    else if (c is ' ' or ',' && letters.Length > 0) break; // first reading only
                }
                if (letters.Length == 0) continue;
                // Some IME versions use 'v' or 'ü'; normalize
                var s = letters.ToString().Replace('ü', 'v');
                full[i] = s;
                if (initials[i] == 0) initials[i] = (byte)s[0];
                filled++;
            }
            ife.Close();
            if (filled < 1000) { Logger.Info($"IME pinyin enrichment returned only {filled} chars; ignored"); SaveCache(); return; }
            lock (s_gate)
            {
                initials.CopyTo(s_initials, 0);
                s_full = full;
                Version++;
            }
            Logger.Info($"Pinyin enriched via IME: {filled} chars in {sw.ElapsedMilliseconds}ms");
            SaveCache();
            try { Updated?.Invoke(); } catch { }
        }
        catch (Exception ex)
        {
            s_imeTried = true;
            Logger.Info("IME pinyin enrichment unavailable: " + ex.Message);
            // Still cache the GB2312 table so next start is instant.
            SaveCache();
        }
        finally
        {
            if (obj != null && Marshal.IsComObject(obj)) Marshal.ReleaseComObject(obj);
        }
    }
}

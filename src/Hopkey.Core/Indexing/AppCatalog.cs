using System.Text;
using Hopkey.Core.Pinyin;
using Hopkey.Core.Util;

namespace Hopkey.Core.Indexing;

public sealed class AppEntry
{
    public required string Name { get; init; }
    /// <summary>File path of .lnk/.exe/.url/.appref-ms, or "shell:AppsFolder\{AUMID}" for UWP.</summary>
    public required string Path { get; init; }
    public required string Lower { get; init; }
    public required string Initials { get; init; }   // pinyin initials (== Lower when no CJK)
    public required string Acronym { get; init; }    // "visual studio code" → "vsc"
    public string? FullPinyin { get; init; }
    public bool IsUwp { get; init; }
}

/// <summary>Start menu / desktop applications (+ optional UWP apps). Small list, rebuilt on change.</summary>
public sealed class AppCatalog : IDisposable
{
    private static readonly string[] s_extensions = { ".lnk", ".exe", ".url", ".appref-ms" };
    private volatile IReadOnlyList<AppEntry> _apps = Array.Empty<AppEntry>();
    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly Timer _reloadTimer;
    private bool _includeUwp;

    public IReadOnlyList<AppEntry> Apps => _apps;
    public event Action? Changed;

    public AppCatalog()
    {
        _reloadTimer = new Timer(_ => Reload(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public static IEnumerable<string> SourceFolders()
    {
        yield return Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.StartMenu);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
    }

    public void Start(bool includeUwp)
    {
        _includeUwp = includeUwp;
        Reload();
        foreach (var dir in SourceFolders().Where(Directory.Exists))
        {
            try
            {
                var w = new FileSystemWatcher(dir) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName };
                FileSystemEventHandler h = (_, _) => _reloadTimer.Change(2000, Timeout.Infinite);
                w.Created += h; w.Deleted += h; w.Renamed += (_, _) => _reloadTimer.Change(2000, Timeout.Infinite);
                w.EnableRaisingEvents = true;
                _watchers.Add(w);
            }
            catch (Exception ex) { Logger.Warn($"Cannot watch {dir}", ex); }
        }
    }

    public void SetIncludeUwp(bool include)
    {
        if (include == _includeUwp) return;
        _includeUwp = include;
        _reloadTimer.Change(100, Timeout.Infinite);
    }

    public void Reload()
    {
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var list = new List<AppEntry>(512);
            foreach (var dir in SourceFolders())
            {
                if (!Directory.Exists(dir)) continue;
                IEnumerable<string> files;
                try
                {
                    files = Directory.EnumerateFiles(dir, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true });
                }
                catch { continue; }
                foreach (var f in files)
                {
                    var ext = System.IO.Path.GetExtension(f);
                    if (!s_extensions.Contains(ext, StringComparer.OrdinalIgnoreCase)) continue;
                    var name = System.IO.Path.GetFileNameWithoutExtension(f);
                    if (IsUninstaller(name)) continue;
                    if (!seen.Add(name)) continue; // de-dup by display name (user/common start menu)
                    list.Add(Create(name, f, false));
                }
            }
            if (_includeUwp)
            {
                foreach (var (name, aumid) in EnumerateUwpApps())
                {
                    if (!seen.Add(name)) continue;
                    list.Add(Create(name, @"shell:AppsFolder\" + aumid, true));
                }
            }
            _apps = list;
            Logger.Info($"App catalog: {list.Count} apps in {sw.ElapsedMilliseconds}ms");
            Changed?.Invoke();
        }
        catch (Exception ex) { Logger.Error("App catalog reload failed", ex); }
    }

    private static bool IsUninstaller(string name) =>
        name.Contains("uninstall", StringComparison.OrdinalIgnoreCase) || name.Contains("卸载", StringComparison.Ordinal) ||
        name.Contains("unins000", StringComparison.OrdinalIgnoreCase);

    public static AppEntry Create(string name, string path, bool uwp) => new()
    {
        Name = name,
        Path = path,
        Lower = name.ToLowerInvariant(),
        Initials = PinyinHelper.GetInitials(name),
        Acronym = BuildAcronym(name),
        FullPinyin = PinyinHelper.GetFullPinyin(name),
        IsUwp = uwp,
    };

    /// <summary>Word-initials: word starts after space/_/-/./( and at lower→upper and letter↔digit transitions; CJK → pinyin initial.</summary>
    public static string BuildAcronym(string name)
    {
        var sb = new StringBuilder(8);
        char prev = ' ';
        foreach (var c in name)
        {
            bool boundary = prev is ' ' or '_' or '-' or '.' or '(' or '[' or '&' or '+'
                            || (char.IsUpper(c) && char.IsLower(prev))
                            || (char.IsDigit(c) != char.IsDigit(prev) && char.IsLetterOrDigit(prev));
            if (PinyinHelper.IsCjk(c))
            {
                var b = PinyinInitialsTable.GetInitial(c);
                if (b != 0) sb.Append((char)b);
            }
            else if (boundary && char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            prev = c;
        }
        return sb.ToString();
    }

    private static List<(string Name, string Aumid)> EnumerateUwpApps()
    {
        var result = new List<(string, string)>();
        var t = new Thread(() =>
        {
            try
            {
                var type = Type.GetTypeFromProgID("Shell.Application");
                if (type == null) return;
                dynamic shell = Activator.CreateInstance(type)!;
                dynamic folder = shell.NameSpace("shell:AppsFolder");
                if (folder == null) return;
                foreach (dynamic item in folder.Items())
                {
                    string name = item.Name;
                    string path = item.Path;
                    // Only packaged apps (AUMID contains '!'); desktop apps are covered by the start menu scan.
                    if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(path) && path.Contains('!') && !IsUninstaller(name))
                        result.Add((name, path));
                }
            }
            catch (Exception ex) { Logger.Warn("UWP app enumeration failed", ex); }
        }) { IsBackground = true, Name = "CL-Uwp" };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join(10_000);
        return result;
    }

    public void Dispose()
    {
        foreach (var w in _watchers) w.Dispose();
        _watchers.Clear();
        _reloadTimer.Dispose();
    }
}

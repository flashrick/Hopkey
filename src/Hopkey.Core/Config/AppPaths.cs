namespace Hopkey.Core.Config;

public static class AppPaths
{
    public const string AppName = "Hopkey";

    public static string RoamingDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppName);

    public static string LocalDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppName);

    public static string LogDirectory => Path.Combine(LocalDirectory, "logs");
    public static string SettingsFile => Path.Combine(RoamingDirectory, "settings.json");
    public static string HistoryFile => Path.Combine(RoamingDirectory, "history.json");
    public static string IndexFile => Path.Combine(LocalDirectory, "index.bin");
    public static string PinyinCacheFile => Path.Combine(LocalDirectory, "pinyin-initials.bin");

    public static string ExecutablePath => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, AppName + ".exe");

    /// <summary>Name the app had before it became Hopkey (data folders, Run value, scheduled task).</summary>
    public const string LegacyName = "CopyListary";

    /// <summary>
    /// One-time move of the CopyListary data folders (settings, history, index cache, logs) to the Hopkey ones.
    /// Only when the new folder doesn't exist yet; a folder still in use (old version running) is copied file by file
    /// for the roaming data and left alone for the local cache, which is simply rebuilt.
    /// </summary>
    public static void MigrateFromLegacy()
    {
        Migrate(Environment.SpecialFolder.ApplicationData, RoamingDirectory, copyFilesIfBusy: true);
        Migrate(Environment.SpecialFolder.LocalApplicationData, LocalDirectory, copyFilesIfBusy: false);
    }

    private static void Migrate(Environment.SpecialFolder root, string target, bool copyFilesIfBusy)
    {
        try
        {
            var legacy = Path.Combine(Environment.GetFolderPath(root), LegacyName);
            if (Directory.Exists(target) || !Directory.Exists(legacy)) return;
            try { Directory.Move(legacy, target); }
            catch (IOException) when (copyFilesIfBusy)
            {
                Directory.CreateDirectory(target);
                foreach (var f in Directory.EnumerateFiles(legacy))
                    File.Copy(f, Path.Combine(target, Path.GetFileName(f)), overwrite: false);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        catch { /* never block startup on migration */ }
    }

    public static void EnsureDirectories()
    {
        Directory.CreateDirectory(RoamingDirectory);
        Directory.CreateDirectory(LocalDirectory);
        Directory.CreateDirectory(LogDirectory);
    }

    /// <summary>Atomic write: write to .tmp then replace.</summary>
    public static void AtomicWrite(string path, ReadOnlySpan<byte> data)
    {
        var tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            fs.Write(data);
        if (File.Exists(path)) File.Replace(tmp, path, null, true);
        else File.Move(tmp, path);
    }
}

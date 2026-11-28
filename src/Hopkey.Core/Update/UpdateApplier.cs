using Hopkey.Core.Util;

namespace Hopkey.Core.Update;

/// <summary>
/// Copies a staged release over the install folder, whatever that folder is called. Every file it overwrites is
/// backed up first; if any copy fails, the files already replaced are restored, so the old version keeps working.
/// Files in the install folder that the new release doesn't contain are left alone.
/// </summary>
public static class UpdateApplier
{
    /// <summary>Files a staged release must contain before it may replace anything.</summary>
    public static readonly string[] RequiredFiles = { "Hopkey.exe", "Hopkey.dll", "Hopkey.Core.dll", "Hopkey.runtimeconfig.json", "Hopkey.deps.json" };

    public static void ValidateStage(string stage)
    {
        foreach (var f in RequiredFiles)
            if (!File.Exists(Path.Combine(stage, f)))
                throw new InvalidDataException($"Update package is incomplete: {f} is missing");
    }

    /// <summary>True if this process can create files in <paramref name="dir"/> (else the updater needs elevation).</summary>
    public static bool CanWrite(string dir)
    {
        try
        {
            var probe = Path.Combine(dir, $".hopkey-write-test-{Environment.ProcessId}");
            using (File.Create(probe, 1, FileOptions.DeleteOnClose)) { }
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// Replace the files of <paramref name="target"/> with those of <paramref name="stage"/>. Retries locked files for
    /// up to <paramref name="lockTimeout"/> (the old process may still be releasing them). Throws after rolling back.
    /// </summary>
    public static int Apply(string stage, string target, string backup, TimeSpan lockTimeout)
    {
        ValidateStage(stage);
        if (Directory.Exists(backup)) Directory.Delete(backup, true);
        Directory.CreateDirectory(backup);
        var done = new List<(string Dest, string? Saved)>();
        try
        {
            foreach (var src in Directory.EnumerateFiles(stage, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(stage, src);
                var dest = Path.Combine(target, rel);
                var saved = File.Exists(dest) ? Path.Combine(backup, rel) : null;
                Retry(() =>
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    if (saved != null)
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(saved)!);
                        File.Copy(dest, saved, overwrite: true);
                    }
                    File.Copy(src, dest, overwrite: true);
                }, lockTimeout);
                done.Add((dest, saved));
            }
            Logger.Info($"Update applied: {done.Count} file(s) copied into {target}");
            return done.Count;
        }
        catch (Exception ex)
        {
            Logger.Error($"Update failed after {done.Count} file(s); rolling back", ex);
            for (int i = done.Count - 1; i >= 0; i--)
            {
                var (dest, saved) = done[i];
                try
                {
                    if (saved != null) File.Copy(saved, dest, overwrite: true);
                    else File.Delete(dest);
                }
                catch (Exception rex) { Logger.Error($"Rollback of {dest} failed", rex); }
            }
            throw;
        }
    }

    private static void Retry(Action action, TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (true)
        {
            try { action(); return; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && DateTime.UtcNow < until)
            {
                Thread.Sleep(250);
            }
        }
    }
}

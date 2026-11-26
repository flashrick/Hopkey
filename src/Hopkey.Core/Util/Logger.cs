using System.Collections.Concurrent;
using System.Text;
using Hopkey.Core.Config;

namespace Hopkey.Core.Util;

public enum LogLevel { Debug, Info, Warn, Error }

/// <summary>Asynchronous file logger. Daily files, 7-day retention, 10MB cap per file.</summary>
public static class Logger
{
    private static readonly BlockingCollection<string> s_queue = new(new ConcurrentQueue<string>(), 10_000);
    private static Thread? s_thread;
    private static string? s_dir;
    private const long MaxFileSize = 10 * 1024 * 1024;
    private static volatile bool s_writing;
    public static LogLevel MinLevel { get; set; } = LogLevel.Info;

    public static void Initialize()
    {
        if (s_thread != null) return;
        s_dir = AppPaths.LogDirectory;
        Directory.CreateDirectory(s_dir);
        Cleanup();
        s_thread = new Thread(WriterLoop) { IsBackground = true, Name = "CL-Logger", Priority = ThreadPriority.BelowNormal };
        s_thread.Start();
    }

    public static void Debug(string msg) => Write(LogLevel.Debug, msg, null);
    public static void Info(string msg) => Write(LogLevel.Info, msg, null);
    public static void Warn(string msg, Exception? ex = null) => Write(LogLevel.Warn, msg, ex);
    public static void Error(string msg, Exception? ex = null) => Write(LogLevel.Error, msg, ex);

    public static void Write(LogLevel level, string msg, Exception? ex)
    {
        if (level < MinLevel) return;
        var sb = new StringBuilder(128);
        sb.Append(DateTime.Now.ToString("HH:mm:ss.fff")).Append(" [").Append(level.ToString().ToUpperInvariant()).Append("] [")
          .Append(Environment.CurrentManagedThreadId).Append("] ").Append(msg);
        if (ex != null) sb.AppendLine().Append(ex);
        s_queue.TryAdd(sb.ToString());
    }

    /// <summary>Synchronous write, used for crash logs where the process is about to die.</summary>
    public static void WriteCrash(string text)
    {
        try
        {
            var dir = s_dir ?? AppPaths.LogDirectory;
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, $"crash-{DateTime.Now:yyyyMMdd-HHmmss}.log"), text);
        }
        catch { }
    }

    public static void Flush(int timeoutMs = 1000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while ((s_queue.Count > 0 || s_writing) && sw.ElapsedMilliseconds < timeoutMs) Thread.Sleep(10);
    }

    private static void WriterLoop()
    {
        // Two instances can write the same daily file at once (restart as administrator, a second instance signalling
        // the first). A FileStream opened for Append only seeks to the end once, so each process would overwrite the
        // other's lines: collect the pending lines and seek to the current end right before every write.
        FileStream? fs = null;
        string? currentFile = null;
        var sb = new StringBuilder(4096);
        var enc = new UTF8Encoding(false);
        foreach (var first in s_queue.GetConsumingEnumerable())
        {
            try
            {
                s_writing = true;
                sb.Clear().Append(first).Append("\r\n");
                while (sb.Length < 64 * 1024 && s_queue.TryTake(out var more)) sb.Append(more).Append("\r\n");
                var file = Path.Combine(s_dir!, DateTime.Now.ToString("yyyy-MM-dd") + ".log");
                if (file != currentFile || fs == null)
                {
                    fs?.Dispose();
                    fs = OpenLog(file);
                    currentFile = file;
                }
                var bytes = enc.GetBytes(sb.ToString());
                fs.Seek(0, SeekOrigin.End);
                fs.Write(bytes, 0, bytes.Length);
                fs.Flush();
                if (fs.Length > MaxFileSize)
                {
                    fs.Dispose();
                    fs = null;
                    var rolled = Path.Combine(s_dir!, $"{Path.GetFileNameWithoutExtension(file)}.{DateTime.Now:HHmmss}.log");
                    File.Move(file, rolled, true);
                }
            }
            catch
            {
                try { fs?.Dispose(); } catch { }
                fs = null;
            }
            finally { s_writing = false; }
        }
    }

    private static FileStream OpenLog(string file) =>
        new(file, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.None);

    private static void Cleanup()
    {
        try
        {
            var limit = DateTime.Now.AddDays(-7);
            foreach (var f in Directory.EnumerateFiles(s_dir!, "*.log"))
                if (File.GetLastWriteTime(f) < limit) File.Delete(f);
        }
        catch { }
    }
}

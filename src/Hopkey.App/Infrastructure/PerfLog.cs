using System.Collections.Concurrent;
using System.Diagnostics;
using Hopkey.Core.Util;

namespace Hopkey.App.Infrastructure;

/// <summary>Timing points for the performance targets in PLAN §9.1. Values > 2× target are logged as warnings.</summary>
public static class PerfLog
{
    public static readonly ConcurrentDictionary<string, double> Last = new();

    private static readonly Dictionary<string, double> s_targets = new()
    {
        ["startup.tray"] = 400,
        ["launcher.show"] = 50,
        ["search.query"] = 10,
        ["search.render"] = 30,
        ["quickswitch.toolbar"] = 100,
        ["quickswitch.navigate"] = 150,
        ["index.load"] = 1000,
    };

    public static long Start() => Stopwatch.GetTimestamp();

    public static double Stop(string name, long start)
    {
        double ms = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        Record(name, ms);
        return ms;
    }

    public static void Record(string name, double ms)
    {
        Last[name] = ms;
        if (s_targets.TryGetValue(name, out var target) && ms > target * 2)
            Logger.Warn($"[perf] {name} took {ms:F1}ms (target {target}ms)");
        else
            Logger.Debug($"[perf] {name} {ms:F1}ms");
    }

    public static string Summary() =>
        string.Join(Environment.NewLine, Last.OrderBy(k => k.Key).Select(k => $"{k.Key}: {k.Value:F1} ms"));
}

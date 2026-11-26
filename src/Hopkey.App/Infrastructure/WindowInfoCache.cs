using System.Collections.Concurrent;
using Hopkey.Core.Native;

namespace Hopkey.App.Infrastructure;

public sealed record WindowInfo(IntPtr Hwnd, string ClassName, uint ProcessId, uint ThreadId, string ProcessName, bool Elevated);

/// <summary>hwnd → class/process/elevation cache, invalidated on EVENT_OBJECT_DESTROY.</summary>
public static class WindowInfoCache
{
    private static readonly ConcurrentDictionary<IntPtr, WindowInfo> s_cache = new();

    public static WindowInfo Get(IntPtr hwnd)
    {
        if (s_cache.TryGetValue(hwnd, out var info)) return info;
        var cls = User32.GetClassName(hwnd);
        uint tid = User32.GetWindowThreadProcessId(hwnd, out uint pid);
        var pname = Kernel32.GetProcessName(pid);
        bool elevated = ElevationHelper.IsBlockedByUipi(pid);
        info = new WindowInfo(hwnd, cls, pid, tid, pname, elevated);
        if (s_cache.Count > 4096) s_cache.Clear();
        s_cache[hwnd] = info;
        return info;
    }

    public static void Invalidate(IntPtr hwnd) => s_cache.TryRemove(hwnd, out _);

    public static bool IsExplorer(IntPtr hwnd)
    {
        var cls = User32.GetClassName(hwnd);
        return cls is "CabinetWClass" or "ExploreWClass";
    }
}

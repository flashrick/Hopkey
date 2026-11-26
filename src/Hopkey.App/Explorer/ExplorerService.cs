using System.Runtime.InteropServices;
using Hopkey.App.Infrastructure;
using Hopkey.Core.History;
using Hopkey.Core.Native;
using Hopkey.Core.Util;
using IServiceProvider = Hopkey.Core.Native.IServiceProvider;

namespace Hopkey.App.Explorer;

public sealed record ExplorerWindowInfo(IntPtr Hwnd, string? Path, string Title, bool IsFileSystem);

public sealed record ExplorerActivation(IntPtr Hwnd, DateTime AtUtc, long Seq);

/// <summary>
/// Shell.Application/ShellWindows access on the dedicated CL-Shell STA thread. The window list is cached and refreshed
/// asynchronously on Explorer activation/title change; hot paths (menus, Ctrl+G, auto-switch) read the cache.
/// Windows 11 tabs: the active tab is the first ShellTabWindowClass child; each ShellWindows item's IShellBrowser
/// window handle is matched against it.
/// </summary>
public sealed class ExplorerService : IDisposable
{
    private DispatcherThread _shell;
    private volatile IReadOnlyList<ExplorerWindowInfo> _cached = Array.Empty<ExplorerWindowInfo>();
    private volatile ExplorerActivation? _lastActive;
    private long _seq;
    private int _refreshQueued;
    private readonly HistoryStore _history;

    public IReadOnlyList<ExplorerWindowInfo> Cached => _cached;
    public ExplorerActivation? LastActive => _lastActive;
    public event Action<string>? FolderVisited;

    public ExplorerService(HistoryStore history)
    {
        _history = history;
        _shell = new DispatcherThread("CL-Shell");
    }

    /// <summary>Called on the UI thread from the foreground hook. Lock-free.</summary>
    public ExplorerActivation NotifyActivated(IntPtr hwnd)
    {
        var a = new ExplorerActivation(hwnd, DateTime.UtcNow, Interlocked.Increment(ref _seq));
        _lastActive = a;
        QueueRefresh(recordVisitFor: hwnd);
        return a;
    }

    public void NotifyTitleChanged(IntPtr hwnd) => QueueRefresh(recordVisitFor: hwnd, delayMs: 400);

    public ExplorerWindowInfo? GetCached(IntPtr hwnd)
    {
        foreach (var w in _cached) if (w.Hwnd == hwnd) return w;
        return null;
    }

    public void QueueRefresh(IntPtr recordVisitFor = default, int delayMs = 0)
    {
        if (Interlocked.Exchange(ref _refreshQueued, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            if (delayMs > 0) await Task.Delay(delayMs).ConfigureAwait(false);
            Interlocked.Exchange(ref _refreshQueued, 0);
            try
            {
                var list = await GetWindowsAsync(CancellationToken.None).ConfigureAwait(false);
                if (recordVisitFor != IntPtr.Zero)
                {
                    var w = list.FirstOrDefault(x => x.Hwnd == recordVisitFor);
                    if (w?.Path != null && w.IsFileSystem)
                    {
                        _history.RecordFolderVisit(w.Path, FolderVisitSource.Explorer);
                        FolderVisited?.Invoke(w.Path);
                    }
                }
            }
            catch (Exception ex) { Logger.Debug("Explorer refresh failed: " + ex.Message); }
        });
    }

    /// <summary>Full refresh, ordered by z-order (top → bottom), active tab per window.</summary>
    public async Task<IReadOnlyList<ExplorerWindowInfo>> GetWindowsAsync(CancellationToken ct)
    {
        var list = await InvokeShell(EnumerateWindows, ct).ConfigureAwait(false);
        _cached = list;
        return list;
    }

    public async Task<string?> GetFolderAsync(IntPtr hwnd, CancellationToken ct)
    {
        var list = await GetWindowsAsync(ct).ConfigureAwait(false);
        var w = list.FirstOrDefault(x => x.Hwnd == hwnd);
        return w is { IsFileSystem: true } ? w.Path : null;
    }

    public Task<bool> NavigateAsync(IntPtr hwnd, string folder, CancellationToken ct) =>
        InvokeShell(() => Navigate(hwnd, folder), ct);

    public async Task<bool> NavigateAndSelectAsync(IntPtr hwnd, string filePath, CancellationToken ct)
    {
        var parent = PathUtil.GetParent(filePath);
        if (parent == null) return false;
        if (!await NavigateAsync(hwnd, parent, ct).ConfigureAwait(false)) return false;
        var name = Path.GetFileName(filePath);
        // Wait for the view to load, then select.
        for (int i = 0; i < 30; i++)
        {
            await Task.Delay(50, ct).ConfigureAwait(false);
            bool ok = await InvokeShell(() => TrySelect(hwnd, parent, name), ct).ConfigureAwait(false);
            if (ok) return true;
        }
        return false;
    }

    // ------------------------------------------------------------------ shell thread implementation

    private async Task<T> InvokeShell<T>(Func<T> func, CancellationToken ct)
    {
        try
        {
            return await _shell.InvokeAsync(func, TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // Explorer hung: abandon the thread (it may be stuck in COM) and create a fresh one.
            Logger.Warn("CL-Shell timed out; recreating shell thread");
            var old = _shell;
            _shell = new DispatcherThread("CL-Shell");
            _ = Task.Run(() => { try { old.Dispose(); } catch { } });
            throw;
        }
    }

    private static object? CreateShellWindows()
    {
        var t = Type.GetTypeFromCLSID(ShellGuids.CLSID_ShellWindows);
        return t == null ? null : Activator.CreateInstance(t);
    }

    private sealed record ShellItem(object Com, IntPtr TopHwnd, IntPtr TabHwnd);

    /// <summary>Enumerate ShellWindows items that belong to Explorer windows. Caller must release COM objects.</summary>
    private static List<ShellItem> EnumerateItems()
    {
        var items = new List<ShellItem>();
        var windowsObj = CreateShellWindows();
        if (windowsObj == null) return items;
        try
        {
            dynamic windows = windowsObj;
            int count = windows.Count;
            for (int i = 0; i < count; i++)
            {
                object? item = null;
                try
                {
                    item = windows.Item(i);
                    if (item == null) continue;
                    dynamic d = item;
                    IntPtr top = new((long)d.HWND);
                    if (!WindowInfoCache.IsExplorer(top)) { Marshal.ReleaseComObject(item); continue; }
                    items.Add(new ShellItem(item, top, GetTabHwnd(item)));
                }
                catch
                {
                    if (item != null && Marshal.IsComObject(item)) Marshal.ReleaseComObject(item);
                }
            }
        }
        finally { Marshal.ReleaseComObject(windowsObj); }
        return items;
    }

    private static IntPtr GetTabHwnd(object item)
    {
        try
        {
            if (item is not IServiceProvider sp) return IntPtr.Zero;
            var sid = ShellGuids.SID_STopLevelBrowser;
            var iid = ShellGuids.IID_IShellBrowser;
            if (sp.QueryService(ref sid, ref iid, out var obj) != 0 || obj is not IShellBrowser sb) return IntPtr.Zero;
            try { return sb.GetWindow(out var h) == 0 ? h : IntPtr.Zero; }
            finally { Marshal.ReleaseComObject(sb); }
        }
        catch { return IntPtr.Zero; }
    }

    private static IShellBrowser? GetShellBrowser(object item)
    {
        if (item is not IServiceProvider sp) return null;
        var sid = ShellGuids.SID_STopLevelBrowser;
        var iid = ShellGuids.IID_IShellBrowser;
        return sp.QueryService(ref sid, ref iid, out var obj) == 0 ? obj as IShellBrowser : null;
    }

    private static IntPtr ActiveTabOf(IntPtr top) => User32.FindWindowEx(top, IntPtr.Zero, "ShellTabWindowClass", null);

    /// <summary>For each top-level window pick the item whose tab is active (fallback: first item of the window).</summary>
    private static Dictionary<IntPtr, ShellItem> PickActive(List<ShellItem> items)
    {
        var map = new Dictionary<IntPtr, ShellItem>();
        foreach (var g in items.GroupBy(i => i.TopHwnd))
        {
            var active = ActiveTabOf(g.Key);
            var pick = g.FirstOrDefault(i => active != IntPtr.Zero && i.TabHwnd == active) ?? g.First();
            map[g.Key] = pick;
        }
        return map;
    }

    private static string? GetItemPath(object item)
    {
        try
        {
            dynamic d = item;
            dynamic doc = d.Document;
            if (doc == null) return null;
            string? path = doc.Folder?.Self?.Path;
            return path;
        }
        catch { return null; }
    }

    private static List<IntPtr> ZOrderExplorers()
    {
        var list = new List<IntPtr>();
        User32.EnumWindows((h, _) =>
        {
            if (User32.IsWindowVisible(h) && WindowInfoCache.IsExplorer(h)) list.Add(h);
            return true;
        }, IntPtr.Zero);
        return list;
    }

    private static IReadOnlyList<ExplorerWindowInfo> EnumerateWindows()
    {
        var items = EnumerateItems();
        try
        {
            var active = PickActive(items);
            var z = ZOrderExplorers();
            var result = new List<ExplorerWindowInfo>(active.Count);
            foreach (var hwnd in z)
            {
                if (!active.TryGetValue(hwnd, out var item)) continue;
                var path = GetItemPath(item.Com);
                bool fs = PathUtil.IsFileSystemPath(path);
                result.Add(new ExplorerWindowInfo(hwnd, path, User32.GetWindowText(hwnd), fs));
            }
            return result;
        }
        finally
        {
            foreach (var i in items) if (Marshal.IsComObject(i.Com)) Marshal.ReleaseComObject(i.Com);
        }
    }

    private static bool Navigate(IntPtr hwnd, string folder)
    {
        var items = EnumerateItems();
        try
        {
            if (!PickActive(items).TryGetValue(hwnd, out var item)) return false;
            // Preferred: IShellBrowser.BrowseObject with a PIDL (same tab).
            var sb = GetShellBrowser(item.Com);
            if (sb != null)
            {
                try
                {
                    if (Shell32.SHParseDisplayName(folder, IntPtr.Zero, out var pidl, 0, out _) == 0 && pidl != IntPtr.Zero)
                    {
                        try
                        {
                            if (sb.BrowseObject(pidl, Shell32.SBSP_SAMEBROWSER | Shell32.SBSP_ABSOLUTE) == 0) return true;
                        }
                        finally { Shell32.ILFree(pidl); }
                    }
                }
                finally { Marshal.ReleaseComObject(sb); }
            }
            // Fallback: IWebBrowser2.Navigate2
            dynamic d = item.Com;
            d.Navigate2(folder);
            return true;
        }
        catch (Exception ex)
        {
            Logger.Warn($"Explorer navigate failed: {folder}", ex);
            return false;
        }
        finally
        {
            foreach (var i in items) if (Marshal.IsComObject(i.Com)) Marshal.ReleaseComObject(i.Com);
        }
    }

    private static bool TrySelect(IntPtr hwnd, string folder, string name)
    {
        var items = EnumerateItems();
        try
        {
            if (!PickActive(items).TryGetValue(hwnd, out var item)) return false;
            if (!PathUtil.Equal(GetItemPath(item.Com), folder)) return false;
            dynamic d = item.Com;
            dynamic doc = d.Document;
            dynamic? fi = doc.Folder.ParseName(name);
            if (fi == null) return false;
            // SVSI_SELECT | SVSI_DESELECTOTHERS | SVSI_ENSUREVISIBLE | SVSI_FOCUSED
            doc.SelectItem(fi, 1 | 4 | 8 | 16);
            return true;
        }
        catch { return false; }
        finally
        {
            foreach (var i in items) if (Marshal.IsComObject(i.Com)) Marshal.ReleaseComObject(i.Com);
        }
    }

    public void Dispose() => _shell.Dispose();
}

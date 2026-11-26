using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Hopkey.App.Infrastructure;
using Hopkey.Core.Native;
using Hopkey.Core.Util;

namespace Hopkey.App.Launcher;

/// <summary>
/// Icons never load on the UI thread. Per-extension icons via SHGetFileInfo(SHGFI_USEFILEATTRIBUTES) (no disk access);
/// per-path icons only for .exe/.lnk/.ico/.url/.appref-ms, drives and shell: items via IShellItemImageFactory on the
/// CL-Icons STA thread. A placeholder is returned immediately; the callback delivers the real (frozen) image.
/// </summary>
public sealed class IconCache : IDisposable
{
    private static readonly HashSet<string> s_perPathExt = new(StringComparer.OrdinalIgnoreCase) { ".exe", ".lnk", ".ico", ".url", ".appref-ms", ".msc", ".cpl", ".scr" };
    private readonly DispatcherThread _thread = new("CL-Icons", ThreadPriority.BelowNormal);
    private readonly ConcurrentDictionary<string, ImageSource> _byExt = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ImageSource> _byPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<string> _pathOrder = new();
    private const int MaxPathEntries = 1024;

    public ImageSource Placeholder { get; }
    public ImageSource FolderPlaceholder { get; }

    public IconCache()
    {
        Placeholder = CreatePlaceholder(false);
        FolderPlaceholder = CreatePlaceholder(true);
        // Warm the common icons in the background.
        _thread.Post(() =>
        {
            _byExt["<folder>"] = LoadExtIcon("folder", true) ?? FolderPlaceholder;
            foreach (var e in new[] { ".txt", ".pdf", ".docx", ".xlsx", ".png", ".jpg", ".zip", ".mp4" })
                _byExt[e] = LoadExtIcon(e, false) ?? Placeholder;
        });
    }

    private static ImageSource CreatePlaceholder(bool folder)
    {
        var g = new DrawingGroup();
        using (var dc = g.Open())
        {
            var brush = new SolidColorBrush(folder ? Color.FromRgb(0xF2, 0xC1, 0x4E) : Color.FromRgb(0xB0, 0xB8, 0xC4));
            brush.Freeze();
            if (folder) dc.DrawRoundedRectangle(brush, null, new Rect(2, 6, 28, 20), 3, 3);
            else dc.DrawRoundedRectangle(brush, null, new Rect(7, 3, 18, 26), 2, 2);
        }
        var img = new DrawingImage(g);
        img.Freeze();
        return img;
    }

    /// <summary>Return the best icon available now; if a better one must be loaded, <paramref name="onLoaded"/> is called later (any thread).</summary>
    public ImageSource Get(string? path, bool isFolder, bool isApp, Action<ImageSource>? onLoaded)
    {
        if (string.IsNullOrEmpty(path)) return Placeholder;
        bool shell = path.StartsWith("shell:", StringComparison.OrdinalIgnoreCase);
        string ext = isFolder ? "<folder>" : Path.GetExtension(path);
        bool perPath = shell || (!isFolder && s_perPathExt.Contains(ext)) || (isFolder && path.Length <= 3) || isApp;

        if (perPath)
        {
            if (_byPath.TryGetValue(path, out var cached)) return cached;
            _thread.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () =>
            {
                var img = LoadShellItemIcon(path) ?? (isFolder ? FolderPlaceholder : LoadExtIcon(ext, false) ?? Placeholder);
                AddPath(path, img);
                onLoaded?.Invoke(img);
            });
            return isFolder ? GetExtCached("<folder>") ?? FolderPlaceholder : GetExtCached(ext) ?? Placeholder;
        }

        var hit = GetExtCached(ext);
        if (hit != null) return hit;
        _thread.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () =>
        {
            var img = _byExt.GetOrAdd(ext, e => LoadExtIcon(e == "<folder>" ? "folder" : e, isFolder) ?? (isFolder ? FolderPlaceholder : Placeholder));
            onLoaded?.Invoke(img);
        });
        return isFolder ? FolderPlaceholder : Placeholder;
    }

    private ImageSource? GetExtCached(string ext) => _byExt.TryGetValue(ext, out var img) ? img : null;

    private void AddPath(string path, ImageSource img)
    {
        _byPath[path] = img;
        _pathOrder.Enqueue(path);
        while (_byPath.Count > MaxPathEntries && _pathOrder.TryDequeue(out var old)) _byPath.TryRemove(old, out _);
    }

    /// <summary>Drop per-path icons (memory trim when the launcher has been hidden for a while).</summary>
    public void TrimPathCache()
    {
        _byPath.Clear();
        while (_pathOrder.TryDequeue(out _)) { }
    }

    private static ImageSource? LoadExtIcon(string ext, bool folder)
    {
        try
        {
            var info = new SHFILEINFO();
            uint attr = folder ? Shell32.FILE_ATTRIBUTE_DIRECTORY : Shell32.FILE_ATTRIBUTE_NORMAL;
            string name = folder ? "folder" : "file" + ext;
            var r = Shell32.SHGetFileInfo(name, attr, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(),
                Shell32.SHGFI_ICON | Shell32.SHGFI_LARGEICON | Shell32.SHGFI_USEFILEATTRIBUTES);
            if (r == IntPtr.Zero || info.hIcon == IntPtr.Zero) return null;
            try
            {
                var src = Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                src.Freeze();
                return src;
            }
            finally { User32.DestroyIcon(info.hIcon); }
        }
        catch (Exception ex)
        {
            Logger.Debug($"Icon for {ext} failed: {ex.Message}");
            return null;
        }
    }

    private static ImageSource? LoadShellItemIcon(string path)
    {
        object? obj = null;
        IntPtr hbmp = IntPtr.Zero;
        try
        {
            var iid = ShellGuids.IID_IShellItemImageFactory;
            Shell32.SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out obj);
            if (obj is not IShellItemImageFactory f) return null;
            if (f.GetImage(new SIZE(32, 32), SIIGBF.ICONONLY | SIIGBF.BIGGERSIZEOK, out hbmp) != 0 || hbmp == IntPtr.Zero) return null;
            return HBitmapToSource(hbmp);
        }
        catch { return null; }
        finally
        {
            if (hbmp != IntPtr.Zero) Gdi32.DeleteObject(hbmp);
            if (obj != null && Marshal.IsComObject(obj)) Marshal.ReleaseComObject(obj);
        }
    }

    private static BitmapSource? HBitmapToSource(IntPtr hbmp)
    {
        if (Gdi32.GetObject(hbmp, Marshal.SizeOf<BITMAP>(), out var bm) == 0) return null;
        BitmapSource src;
        if (bm.bmBitsPixel == 32 && bm.bmBits != IntPtr.Zero)
        {
            int stride = bm.bmWidthBytes;
            int h = Math.Abs(bm.bmHeight);
            src = BitmapSource.Create(bm.bmWidth, h, 96, 96, PixelFormats.Pbgra32, null, bm.bmBits, stride * h, stride);
        }
        else
        {
            src = Imaging.CreateBitmapSourceFromHBitmap(hbmp, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
        }
        src.Freeze();
        return src;
    }

    public void Dispose() => _thread.Dispose();
}

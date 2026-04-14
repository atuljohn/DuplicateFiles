using Microsoft.Extensions.Logging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace DuplicatePhotoFinder.Services;

public class ThumbnailService
{
    private readonly ILogger<ThumbnailService> _logger;
    // Bounded LRU. Each frozen 256×256 BitmapSource is ~256 KB; cap ≈ 128 MB total.
    private const int CacheCapacity = 500;
    private readonly LinkedList<string> _lruOrder = new();
    private readonly Dictionary<string, (LinkedListNode<string> node, BitmapSource bitmap)> _cache = new();
    private readonly object _cacheLock = new();
    private const int ThumbnailSize = 256;

    [ComImport]
    [Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        void GetImage(SIZE size, int flags, out IntPtr phbm);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE { public int cx, cy; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(
        string pszPath, IntPtr pbc, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IShellItemImageFactory ppv);

    private static readonly Guid IID_IShellItemImageFactory = new("bcc18b79-ba16-442f-80c4-8a59c30c463b");

    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr hObject);

    public ThumbnailService(ILogger<ThumbnailService> logger)
    {
        _logger = logger;
    }

    public async Task<BitmapSource?> GetThumbnailAsync(string path, CancellationToken ct = default)
    {
        lock (_cacheLock)
        {
            if (_cache.TryGetValue(path, out var entry))
            {
                _lruOrder.Remove(entry.node);
                _lruOrder.AddFirst(entry.node);
                return entry.bitmap;
            }
        }

        BitmapSource? result = null;
        try
        {
            result = await Task.Run(() => GetShellThumbnail(path), ct);
        }
        catch { }

        if (result == null)
        {
            try
            {
                result = await Task.Run(() => GetImageSharpThumbnail(path), ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to get thumbnail: {Path}", path);
            }
        }

        if (result != null)
        {
            if (!result.IsFrozen) result.Freeze();
            lock (_cacheLock)
            {
                if (!_cache.ContainsKey(path))
                {
                    var node = _lruOrder.AddFirst(path);
                    _cache[path] = (node, result);
                    while (_cache.Count > CacheCapacity)
                    {
                        var oldest = _lruOrder.Last;
                        if (oldest == null) break;
                        _lruOrder.RemoveLast();
                        _cache.Remove(oldest.Value);
                    }
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Drop all cached thumbnails. Call when leaving the review screen
    /// or when memory pressure is detected.
    /// </summary>
    public void ClearCache()
    {
        lock (_cacheLock)
        {
            _cache.Clear();
            _lruOrder.Clear();
        }
    }

    private BitmapSource? GetShellThumbnail(string path)
    {
        try
        {
            SHCreateItemFromParsingName(path, IntPtr.Zero, IID_IShellItemImageFactory, out var factory);
            factory.GetImage(new SIZE { cx = ThumbnailSize, cy = ThumbnailSize }, 0, out var hbm);
            if (hbm == IntPtr.Zero) return null;
            try
            {
                var bmp = Imaging.CreateBitmapSourceFromHBitmap(hbm, IntPtr.Zero, Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions());
                bmp.Freeze();
                return bmp;
            }
            finally
            {
                DeleteObject(hbm);
            }
        }
        catch { return null; }
    }

    private BitmapSource? GetImageSharpThumbnail(string path)
    {
        try
        {
            using var image = SixLabors.ImageSharp.Image.Load(path);
            image.Mutate(x => x.Resize(new SixLabors.ImageSharp.Processing.ResizeOptions
            {
                Size = new SixLabors.ImageSharp.Size(ThumbnailSize, ThumbnailSize),
                Mode = SixLabors.ImageSharp.Processing.ResizeMode.Max
            }));

            using var ms = new System.IO.MemoryStream();
            image.SaveAsPng(ms);
            ms.Position = 0;

            var decoder = new PngBitmapDecoder(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var frame = decoder.Frames[0];
            frame.Freeze();
            return frame;
        }
        catch { return null; }
    }
}

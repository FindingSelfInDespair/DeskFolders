using System;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DeskFolders.Native;

public sealed class ShellIconExtractor : IDisposable
{
    private const int MaxCacheEntries = 600;
    private static readonly Guid ShellItemImageFactoryGuid = new("bcc18b79-ba16-442f-80c4-8a59c30c463b");

    private readonly ConcurrentDictionary<string, BitmapSource> _cache = new(StringComparer.Ordinal);
    private readonly BlockingCollection<(string Path, int Size, string CacheKey, TaskCompletionSource<BitmapSource?> Tcs)> _queue = new();
    private readonly Thread _staThread;
    private volatile bool _disposed;

    public ShellIconExtractor()
    {
        _staThread = new Thread(WorkerLoop)
        {
            IsBackground = true,
            Name = "ShellIconSTAWorker"
        };
        _staThread.SetApartmentState(ApartmentState.STA);
        _staThread.Start();
    }

    public Task<BitmapSource?> GetIconAsync(string path, int pixelSize)
    {
        if (_disposed || string.IsNullOrWhiteSpace(path))
            return Task.FromResult<BitmapSource?>(null);

        string cacheKey = BuildCacheKey(path, pixelSize);
        if (_cache.TryGetValue(cacheKey, out var cached))
            return Task.FromResult<BitmapSource?>(cached);

        var tcs = new TaskCompletionSource<BitmapSource?>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            _queue.Add((path, pixelSize, cacheKey, tcs));
        }
        catch (InvalidOperationException)
        {
            tcs.TrySetResult(null);
        }

        return tcs.Task;
    }

    public void InvalidatePath(string path)
    {
        string prefix = path.ToLowerInvariant() + "|";
        foreach (var key in _cache.Keys)
        {
            if (key.StartsWith(prefix, StringComparison.Ordinal))
                _cache.TryRemove(key, out _);
        }
    }

    private void WorkerLoop()
    {
        foreach (var work in _queue.GetConsumingEnumerable())
        {
            try
            {
                if (_cache.TryGetValue(work.CacheKey, out var cached))
                {
                    work.Tcs.TrySetResult(cached);
                    continue;
                }

                BitmapSource? bmp = ExtractIconInternal(work.Path, work.Size);
                if (bmp != null)
                {
                    if (_cache.Count >= MaxCacheEntries)
                        _cache.Clear();

                    _cache.TryAdd(work.CacheKey, bmp);
                }

                work.Tcs.TrySetResult(bmp);
            }
            catch
            {
                work.Tcs.TrySetResult(null);
            }
        }
    }

    private static string BuildCacheKey(string path, int size)
    {
        long ticks = 0;
        try
        {
            if (File.Exists(path))
                ticks = File.GetLastWriteTimeUtc(path).Ticks;
            else if (Directory.Exists(path))
                ticks = Directory.GetLastWriteTimeUtc(path).Ticks;
        }
        catch
        {
            ticks = 0;
        }

        return $"{path.ToLowerInvariant()}|{size}|{ticks}";
    }

    private static BitmapSource? ExtractIconInternal(string path, int size)
    {
        NativeMethods.IShellItemImageFactory? factory = null;
        IntPtr hBitmap = IntPtr.Zero;

        try
        {
            int hr = NativeMethods.SHCreateItemFromParsingName(path, IntPtr.Zero, ShellItemImageFactoryGuid, out factory);
            if (hr == 0 && factory != null)
            {
                hr = factory.GetImage(
                    new NativeMethods.SIZE(size, size),
                    NativeMethods.SIIGBF.SIIGBF_ICONONLY | NativeMethods.SIIGBF.SIIGBF_RESIZETOFIT,
                    out hBitmap);

                if (hr == 0 && hBitmap != IntPtr.Zero)
                {
                    var source = CreateBitmapSourcePreservingAlpha(hBitmap);
                    if (source != null)
                    {
                        source.Freeze();
                        return source;
                    }
                }
            }
        }
        catch
        {
            // Fallback to SHGetFileInfo below
        }
        finally
        {
            if (hBitmap != IntPtr.Zero)
                NativeMethods.DeleteObject(hBitmap);

            if (factory != null)
                Marshal.ReleaseComObject(factory);
        }

        return ExtractViaShGetFileInfo(path);
    }

    private static BitmapSource? CreateBitmapSourcePreservingAlpha(IntPtr hBitmap)
    {
        if (NativeMethods.GetObject(hBitmap, Marshal.SizeOf<NativeMethods.BITMAP>(), out var bm) == 0)
            return null;

        if (bm.bmWidth <= 0 || bm.bmHeight <= 0)
            return null;

        if (bm.bmBitsPixel == 32)
        {
            int stride = bm.bmWidth * 4;
            byte[] pixels = new byte[stride * bm.bmHeight];

            var bmi = new NativeMethods.BITMAPINFO
            {
                bmiHeader = new NativeMethods.BITMAPINFOHEADER
                {
                    biSize = (uint)Marshal.SizeOf<NativeMethods.BITMAPINFOHEADER>(),
                    biWidth = bm.bmWidth,
                    biHeight = -bm.bmHeight,
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = NativeMethods.BI_RGB
                }
            };

            IntPtr hdc = NativeMethods.GetDC(IntPtr.Zero);
            int rows = 0;
            try
            {
                rows = NativeMethods.GetDIBits(hdc, hBitmap, 0, (uint)bm.bmHeight, pixels, ref bmi, NativeMethods.DIB_RGB_COLORS);
            }
            finally
            {
                if (hdc != IntPtr.Zero)
                    NativeMethods.ReleaseDC(IntPtr.Zero, hdc);
            }

            if (rows > 0)
            {
                bool hasAlpha = false;
                bool hasNonPremultiplied = false;

                for (int i = 0; i < pixels.Length; i += 4)
                {
                    byte b = pixels[i];
                    byte g = pixels[i + 1];
                    byte r = pixels[i + 2];
                    byte a = pixels[i + 3];

                    if (a != 0)
                        hasAlpha = true;

                    if (r > a || g > a || b > a)
                        hasNonPremultiplied = true;
                }

                if (hasAlpha)
                {
                    PixelFormat format = hasNonPremultiplied ? PixelFormats.Bgra32 : PixelFormats.Pbgra32;
                    return BitmapSource.Create(bm.bmWidth, bm.bmHeight, 96, 96, format, null, pixels, stride);
                }
            }
        }

        return Imaging.CreateBitmapSourceFromHBitmap(
            hBitmap,
            IntPtr.Zero,
            Int32Rect.Empty,
            BitmapSizeOptions.FromEmptyOptions());
    }

    private static BitmapSource? ExtractViaShGetFileInfo(string path)
    {
        var shfi = new NativeMethods.SHFILEINFO();
        IntPtr res = NativeMethods.SHGetFileInfo(
            path,
            0,
            ref shfi,
            (uint)Marshal.SizeOf<NativeMethods.SHFILEINFO>(),
            NativeMethods.SHGFI_ICON | NativeMethods.SHGFI_LARGEICON);

        if (res == IntPtr.Zero || shfi.hIcon == IntPtr.Zero)
            return null;

        try
        {
            var source = Imaging.CreateBitmapSourceFromHIcon(
                shfi.hIcon,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        finally
        {
            NativeMethods.DestroyIcon(shfi.hIcon);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _queue.CompleteAdding();
        _cache.Clear();
    }
}
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using AC.Infrastructure;

namespace AC.App;

/// <summary>
/// Border background from a URL: local:RemoteImage.DecodeWidth="96" local:RemoteImage.Url="{Binding Icon}".
/// Own loading instead of BitmapImage with an http URL: its built-in WPF cache breaks when one URL
/// loads at once with different widths (header and library, list and overlay) and the image stays empty,
/// and on .NET Core it also re-downloads everything on every start.
/// </summary>
public static class RemoteImage
{
    public static readonly DependencyProperty UrlProperty = DependencyProperty.RegisterAttached(
        "Url", typeof(string), typeof(RemoteImage), new PropertyMetadata(null, OnChanged));

    /// <summary>Decode width in pixels (with headroom for high DPI). 0 - as in the file.</summary>
    public static readonly DependencyProperty DecodeWidthProperty = DependencyProperty.RegisterAttached(
        "DecodeWidth", typeof(int), typeof(RemoteImage), new PropertyMetadata(0, OnChanged));

    public static string? GetUrl(DependencyObject d) => (string?)d.GetValue(UrlProperty);
    public static void SetUrl(DependencyObject d, string? value) => d.SetValue(UrlProperty, value);
    public static int GetDecodeWidth(DependencyObject d) => (int)d.GetValue(DecodeWidthProperty);
    public static void SetDecodeWidth(DependencyObject d, int value) => d.SetValue(DecodeWidthProperty, value);

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Border border) return;
        var url = GetUrl(border);
        int width = GetDecodeWidth(border);
        if (string.IsNullOrEmpty(url))
        {
            border.Background = null;
            return;
        }
        if (ImageCache.TryGet(url, width) is { } ready)
        {
            border.Background = Brush(ready);
            return;
        }
        border.Background = null;
        _ = LoadAsync(border, url, width);
    }

    private static async Task LoadAsync(Border border, string url, int width)
    {
        var image = await ImageCache.GetAsync(url, width);
        // the card may have been reused for another achievement while loading
        if (image is null || GetUrl(border) != url || GetDecodeWidth(border) != width) return;
        border.Background = Brush(image);
        border.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
    }

    private static ImageBrush Brush(ImageSource image)
    {
        var brush = new ImageBrush(image) { Stretch = Stretch.UniformToFill };
        brush.Freeze();
        return brush;
    }
}

/// <summary>
/// Images by URL: memory first (URL + width), then disk (cache\images in the data folder), then the network.
/// Called on the UI thread; I/O and decoding run in the background. One request per URL,
/// however many cards wait. A failed URL (no game art) is not retried for 10 minutes.
/// </summary>
internal static class ImageCache
{
    private const int MemoryLimit = 800;
    private const long DiskLimitBytes = 300L * 1024 * 1024;
    private const long DiskTrimToBytes = 200L * 1024 * 1024;

    // A picture from a stranger's guide may be anything: a bigger answer is dropped, not kept in memory.
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20), MaxResponseContentBufferSize = 20 * 1024 * 1024 };
    private static readonly Dictionary<string, BitmapSource> Memory = new();
    private static readonly Dictionary<string, Task<byte[]?>> Downloads = new();
    private static readonly Dictionary<string, DateTime> Failed = new();
    private static readonly Dictionary<string, (int Width, int Height)> Sizes = new();

    public static string Dir { get; } = Path.Combine(AppPaths.DataRoot, "cache", "images");

    public static BitmapSource? TryGet(string url, int width) => Memory.GetValueOrDefault(Key(url, width));

    /// <summary>The size of the file's picture in pixels, known once it has been loaded at any width.</summary>
    public static (int Width, int Height)? SizeOf(string url) => Sizes.TryGetValue(url, out var size) ? size : null;

    public static async Task<BitmapSource?> GetAsync(string url, int width)
    {
        var key = Key(url, width);
        if (Memory.TryGetValue(key, out var hit)) return hit;
        if (Failed.TryGetValue(url, out var failedAt) && DateTime.UtcNow - failedAt < TimeSpan.FromMinutes(10)) return null;

        if (!Downloads.TryGetValue(url, out var download))
        {
            download = LoadBytesAsync(url);
            Downloads[url] = download;
        }
        byte[]? bytes;
        try { bytes = await download; }
        finally
        {
            if (Downloads.TryGetValue(url, out var current) && current == download) Downloads.Remove(url);
        }

        var decoded = bytes is null ? default : await Task.Run(() => Decode(bytes, width, url));
        var image = decoded.Image;
        if (image is null)
        {
            Failed[url] = DateTime.UtcNow;
            // a broken cache file (e.g. an interrupted write) - delete it to download again next time
            try { File.Delete(PathOf(url)); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            return null;
        }
        if (Memory.Count >= MemoryLimit) Memory.Clear();
        Memory[key] = image;
        Sizes[url] = decoded.Size;
        return image;
    }

    /// <summary>Trims an oversized disk cache, oldest first. In background, at startup.</summary>
    public static void TrimInBackground() => Task.Run(() =>
    {
        try
        {
            if (!Directory.Exists(Dir)) return;
            var files = new DirectoryInfo(Dir).GetFiles().OrderBy(f => f.LastWriteTimeUtc).ToList();
            long total = files.Sum(f => f.Length);
            if (total <= DiskLimitBytes) return;
            foreach (var f in files)
            {
                if (total <= DiskTrimToBytes) break;
                total -= f.Length;
                f.Delete();
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            AppLog.Error("Image cache cleanup", e);
        }
    });

    private static async Task<byte[]?> LoadBytesAsync(string url)
    {
        var path = PathOf(url);
        try
        {
            if (File.Exists(path)) return await File.ReadAllBytesAsync(path).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }

        try
        {
            byte[] bytes;
            try
            {
                bytes = await Http.GetByteArrayAsync(url).ConfigureAwait(false);
            }
            catch (Exception e) when (StoreAssets.IsNotFound(e))
            {
                var real = await StoreAssets.ResolveAsync(Http, url).ConfigureAwait(false);
                if (real is null) throw;
                // Saved under the plain URL, so the next start finds it without asking the store again.
                bytes = await Http.GetByteArrayAsync(real).ConfigureAwait(false);
            }
            try
            {
                Directory.CreateDirectory(Dir);
                var tmp = $"{path}.{Guid.NewGuid():N}.tmp";
                await File.WriteAllBytesAsync(tmp, bytes).ConfigureAwait(false);
                File.Move(tmp, path, overwrite: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* fine without the disk cache */ }
            return bytes;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidOperationException or UriFormatException)
        {
            return null;
        }
    }

    private static (BitmapSource? Image, (int Width, int Height) Size) Decode(byte[] bytes, int width, string url)
    {
        try
        {
            // The header alone gives the size: a picture decoded smaller no longer tells it.
            var frame = BitmapDecoder.Create(new MemoryStream(bytes), BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile,
                BitmapCacheOption.None).Frames[0];
            var image = new BitmapImage();
            image.BeginInit();
            image.StreamSource = new MemoryStream(bytes);
            if (width > 0) image.DecodePixelWidth = width;
            image.CacheOption = BitmapCacheOption.OnLoad;
            // No IgnoreImageCache: the WPF cache is keyed by URL, which a stream lacks; it throws ArgumentNullException.
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            image.EndInit();
            image.Freeze();
            return (image, (frame.PixelWidth, frame.PixelHeight));
        }
        catch (Exception e)
        {
            // a broken or unknown-format image must not crash the UI - just do without it
            AppLog.Error("Image " + url, e);
            return default;
        }
    }

    private static string Key(string url, int width) => url + "|" + width;

    private static string PathOf(string url)
    {
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(url)))[..24].ToLowerInvariant();
        var ext = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? Path.GetExtension(uri.AbsolutePath) : "";
        return Path.Combine(Dir, hash + (ext.Length is > 1 and <= 5 ? ext : ".img"));
    }
}

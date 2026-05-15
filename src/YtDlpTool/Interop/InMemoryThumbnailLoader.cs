using System.IO;
using System.Net.Http;
using System.Windows.Media.Imaging;

namespace YtDlpTool.Interop;

/// <summary>
/// Loads remote thumbnail bitmaps via HttpClient into memory and freezes a BitmapImage so
/// it can be assigned cross-thread. We deliberately bypass WPF's default Image.Source URL
/// fetching because that goes through WinINet and writes cache entries to
/// %LOCALAPPDATA%\Microsoft\Windows\INetCache — disk traces we'd rather not leave behind.
/// </summary>
public static class InMemoryThumbnailLoader
{
    // Single shared HttpClient (the recommended pattern) with a short timeout — thumbnails
    // are tiny and we never want the UI to wait long.
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    public static async Task<BitmapImage?> LoadAsync(string url, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out _)) return null;
        try
        {
            var bytes = await Http.GetByteArrayAsync(url, ct).ConfigureAwait(false);

            // BitmapImage construction has to happen on the UI thread because it touches a
            // DependencyObject, but with CacheOption.OnLoad we can immediately Freeze the
            // result so it's safe to share back to any thread.
            return await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                using var ms = new MemoryStream(bytes);
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                bmp.StreamSource = ms;
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            });
        }
        catch
        {
            // Thumbnail loading is best-effort. A broken thumbnail must never cascade into
            // breaking the rest of the UI flow (URL parsing, queue display, etc.).
            return null;
        }
    }
}

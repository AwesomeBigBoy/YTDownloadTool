using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace YtDlpTool.Interop;

/// <summary>
/// Loads remote thumbnail bitmaps via HttpClient into memory and returns a frozen ImageSource
/// safe to assign across threads. We deliberately bypass WPF's default Image.Source URL
/// fetching because that goes through WinINet and writes cache entries to
/// %LOCALAPPDATA%\Microsoft\Windows\INetCache — disk traces we'd rather not leave behind.
/// </summary>
public static class InMemoryThumbnailLoader
{
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        // Some image CDNs (incl. occasional googlevideo edge nodes) 403 requests with empty
        // User-Agent. Cheap insurance — costs nothing on normal hosts.
        c.DefaultRequestHeaders.UserAgent.ParseAdd("YtDlpTool-Thumbnail/1.0");
        return c;
    }

    public static async Task<ImageSource?> LoadAsync(string url, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out _)) return null;
        try
        {
            var bytes = await Http.GetByteArrayAsync(url, ct).ConfigureAwait(false);
            if (bytes.Length == 0) return null;

            // BitmapFrame.Create with OnLoad + the using-stream pattern decodes the entire
            // image into a pixel buffer during the Create call, so the stream can be safely
            // disposed immediately afterwards. The returned frame is frozen so it can be
            // assigned to UI properties from any thread.
            //
            // We previously used BitmapImage + StreamSource + EndInit, but that has a known
            // WPF lifecycle quirk where the stream is re-read at render time despite
            // CacheOption.OnLoad — disposing the stream early produced a blank image with
            // no error. BitmapFrame.Create is the supported alternative.
            return await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                using var ms = new MemoryStream(bytes, writable: false);
                var frame = BitmapFrame.Create(
                    ms,
                    BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreImageCache,
                    BitmapCacheOption.OnLoad);
                if (frame.CanFreeze) frame.Freeze();
                return (ImageSource)frame;
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

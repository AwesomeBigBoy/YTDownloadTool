using System.Net;
using System.Net.Http;
using YtDlpTool.Domain.Updates;

namespace YtDlpTool.Domain.Tests.Updates;

/// <summary>
/// v1.5.2, report 2026-11-12 (1): 修復's 24 MB download failed with "The request was canceled
/// due to the configured HttpClient.Timeout of 30 seconds elapsing" — no reply within 30 s,
/// as when a scanning gateway holds a download until it has checked it — and the same file
/// came in 5 s on the next try. 30 s is the budget for API calls; a download gets its own,
/// longer limits, and says in Chinese which one ran out.
/// </summary>
public sealed class HttpUpdateClientTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ytdlp-http-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            send(request, ct);
    }

    /// <summary>A body whose first byte arrives and then nothing more, until cancelled.</summary>
    private sealed class StallingStream : Stream
    {
        private bool _sentFirst;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (!_sentFirst)
            {
                _sentFirst = true;
                buffer.Span[0] = 42;
                return 1;
            }
            await Task.Delay(Timeout.Infinite, ct);
            return 0;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static Handler NeverAnswers() =>
        new(async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return new HttpResponseMessage(HttpStatusCode.OK); });

    private static HttpUpdateClient Client(Handler handler, int apiMs = 100, int startMs = 5000, int stallMs = 5000) =>
        new("YtDlpTool-tests", handler, TimeSpan.FromMilliseconds(apiMs),
            TimeSpan.FromMilliseconds(startMs), TimeSpan.FromMilliseconds(stallMs));

    [Fact]
    public async Task DownloadAsync_AnAnswerSlowerThanTheApiBudget_StillDownloads()
    {
        var body = new byte[] { 1, 2, 3, 4 };
        using var client = Client(new Handler(async (_, ct) =>
        {
            await Task.Delay(400, ct);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };
        }));
        var dest = Path.Combine(_dir, "yt-dlp-pkg.zip");

        await client.DownloadAsync("https://example.invalid/yt-dlp-pkg.zip", dest, null, CancellationToken.None);

        Assert.Equal(body, await File.ReadAllBytesAsync(dest));
    }

    [Fact]
    public async Task DownloadAsync_NoAnswerInTime_FailsWithAMessageThatSaysSo()
    {
        using var client = Client(NeverAnswers(), startMs: 300);

        var ex = await Assert.ThrowsAsync<TimeoutException>(() => client.DownloadAsync(
            "https://example.invalid/yt-dlp-pkg.zip", Path.Combine(_dir, "a.zip"), null, CancellationToken.None));

        Assert.Contains("沒有開始傳送", ex.Message);
    }

    [Fact]
    public async Task DownloadAsync_ABodyThatStopsArriving_FailsWithAMessageThatSaysSo()
    {
        using var client = Client(new Handler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StallingStream()) })), stallMs: 300);
        // Before v1.5.2 nothing limited the body, so this would have waited for ever.
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var ex = await Assert.ThrowsAsync<TimeoutException>(() => client.DownloadAsync(
            "https://example.invalid/yt-dlp-pkg.zip", Path.Combine(_dir, "b.zip"), null, guard.Token));

        Assert.Contains("沒有收到資料", ex.Message);
    }

    [Fact]
    public async Task DownloadAsync_CancelledByTheCaller_IsNotReportedAsATimeout()
    {
        using var client = Client(NeverAnswers());
        using var cancel = new CancellationTokenSource(200);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.DownloadAsync(
            "https://example.invalid/yt-dlp-pkg.zip", Path.Combine(_dir, "c.zip"), null, cancel.Token));
    }

    [Fact]
    public async Task GetStringAsync_KeepsTheShortApiBudget()
    {
        // An update check still gives up quickly when GitHub does not answer.
        using var client = Client(NeverAnswers(), apiMs: 200);
        var watch = System.Diagnostics.Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<Exception>(() =>
            client.GetStringAsync("https://example.invalid/manifest.json", CancellationToken.None));

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(4), $"gave up after {watch.Elapsed}");
    }
}

using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace YtDlpTool.Domain.Updates;

public sealed class HttpUpdateClient : IUpdateHttpClient, IDisposable
{
    private readonly HttpMessageHandler _handler;

    // API calls — release list, manifest, signatures: small, and an update check should give
    // up quickly when GitHub does not answer.
    private readonly HttpClient _http;

    // v1.5.2: asset downloads. No overall limit (ffmpeg is 100 MB); DownloadAsync limits the
    // wait for the answer and for each read instead.
    private readonly HttpClient _download;
    private readonly TimeSpan _downloadStartTimeout;
    private readonly TimeSpan _downloadStallTimeout;

    public HttpUpdateClient(string userAgent)
        : this(userAgent, new HttpClientHandler { AutomaticDecompression = System.Net.DecompressionMethods.All },
               TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(2))
    {
    }

    /// <summary>
    /// v1.5.2: the handler both clients send through, and the limits (the short constructor
    /// passes the production values). Report 2026-11-12 (1): 修復's 24 MB download got no
    /// reply within the 30 s every request used to have — a gateway that scans downloads
    /// answers only once it has checked the whole file — and the next try took 5 s. A
    /// download may now wait <paramref name="downloadStartTimeout"/> for the answer and
    /// <paramref name="downloadStallTimeout"/> for each piece of the body.
    /// </summary>
    public HttpUpdateClient(string userAgent, HttpMessageHandler handler, TimeSpan apiTimeout,
        TimeSpan downloadStartTimeout, TimeSpan downloadStallTimeout)
    {
        _handler = handler;
        _downloadStartTimeout = downloadStartTimeout;
        _downloadStallTimeout = downloadStallTimeout;
        _http = CreateClient(handler, userAgent, apiTimeout);
        _download = CreateClient(handler, userAgent, Timeout.InfiniteTimeSpan);
    }

    // The same headers as before v1.5.2 for both; only the time limits differ.
    private static HttpClient CreateClient(HttpMessageHandler handler, string userAgent, TimeSpan timeout)
    {
        var client = new HttpClient(handler, disposeHandler: false) { Timeout = timeout };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    public async Task<GitHubReleaseDto?> GetLatestReleaseAsync(string owner, string repo, CancellationToken ct)
    {
        var url = $"https://api.github.com/repos/{owner}/{repo}/releases/latest";
        var stream = await _http.GetStreamAsync(url, ct).ConfigureAwait(false);
        await using var _ = stream.ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync(stream, GitHubJsonContext.Default.GitHubReleaseDto, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<GitHubReleaseDto>> GetRecentReleasesAsync(string owner, string repo, int limit, CancellationToken ct)
    {
        if (limit < 1) limit = 1;
        if (limit > 100) limit = 100;
        var url = $"https://api.github.com/repos/{owner}/{repo}/releases?per_page={limit}";
        var stream = await _http.GetStreamAsync(url, ct).ConfigureAwait(false);
        await using var _ = stream.ConfigureAwait(false);
        var list = await JsonSerializer.DeserializeAsync(stream, GitHubJsonContext.Default.ListGitHubReleaseDto, ct).ConfigureAwait(false);
        return (IReadOnlyList<GitHubReleaseDto>?)list ?? Array.Empty<GitHubReleaseDto>();
    }

    public async Task<string> GetStringAsync(string url, CancellationToken ct) =>
        await _http.GetStringAsync(url, ct).ConfigureAwait(false);

    /// <summary>
    /// Writes <paramref name="url"/> to <paramref name="destPath"/>. A limit running out is a
    /// <see cref="TimeoutException"/> whose message says which one, in words UpdateApplier
    /// shows as it is; the caller's own cancellation stays an OperationCanceledException.
    /// </summary>
    public async Task DownloadAsync(string url, string destPath, IProgress<double>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);

        HttpResponseMessage response;
        using (var answer = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            answer.CancelAfter(_downloadStartTimeout);
            try
            {
                response = await _download.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, answer.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (answer.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"網路在 {Describe(_downloadStartTimeout)}內沒有開始傳送檔案（可能是網路的安全檢查還在檢查它），請稍後再試一次");
            }
        }

        using (response)
        {
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? -1;
            await using var src = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var dst = File.Create(destPath);
            var buffer = new byte[81920];
            long copied = 0;
            using var piece = CancellationTokenSource.CreateLinkedTokenSource(ct);
            while (true)
            {
                piece.CancelAfter(_downloadStallTimeout);
                int read;
                try
                {
                    read = await src.ReadAsync(buffer, piece.Token).ConfigureAwait(false);
                }
                catch (Exception e) when (e is OperationCanceledException or IOException
                                          && piece.IsCancellationRequested && !ct.IsCancellationRequested)
                {
                    throw new TimeoutException(
                        $"網路連續 {Describe(_downloadStallTimeout)}沒有收到資料，下載中斷了，請稍後再試一次");
                }
                if (read == 0) break;
                await dst.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                copied += read;
                if (total > 0 && progress is not null) progress.Report((double)copied / total * 100);
            }
        }
    }

    private static string Describe(TimeSpan limit) =>
        limit >= TimeSpan.FromMinutes(1)
            ? $"{(int)Math.Round(limit.TotalMinutes)} 分鐘"
            : $"{Math.Max(1, (int)Math.Round(limit.TotalSeconds))} 秒";

    public void Dispose()
    {
        _http.Dispose();
        _download.Dispose();
        _handler.Dispose();
    }
}

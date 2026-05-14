using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace YtDlpTool.Domain.Updates;

public sealed class HttpUpdateClient : IUpdateHttpClient, IDisposable
{
    private readonly HttpClient _http;

    public HttpUpdateClient(string userAgent)
    {
        _http = new HttpClient(new HttpClientHandler { AutomaticDecompression = System.Net.DecompressionMethods.All })
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    }

    public async Task<GitHubReleaseDto?> GetLatestReleaseAsync(string owner, string repo, CancellationToken ct)
    {
        var url = $"https://api.github.com/repos/{owner}/{repo}/releases/latest";
        var stream = await _http.GetStreamAsync(url, ct).ConfigureAwait(false);
        await using var _ = stream.ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync(stream, GitHubJsonContext.Default.GitHubReleaseDto, ct).ConfigureAwait(false);
    }

    public async Task<string> GetStringAsync(string url, CancellationToken ct) =>
        await _http.GetStringAsync(url, ct).ConfigureAwait(false);

    public async Task DownloadAsync(string url, string destPath, IProgress<double>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength ?? -1;
        await using var src = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var dst = File.Create(destPath);
        var buffer = new byte[81920];
        long copied = 0;
        int read;
        while ((read = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            await dst.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            copied += read;
            if (total > 0 && progress is not null) progress.Report((double)copied / total * 100);
        }
    }

    public void Dispose() => _http.Dispose();
}

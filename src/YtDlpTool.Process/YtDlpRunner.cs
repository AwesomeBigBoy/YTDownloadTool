using System.Text.Json;
using YtDlpTool.Domain.Logging;
using YtDlpTool.Domain.Models;

namespace YtDlpTool.Process;

public sealed class YtDlpRunner
{
    private readonly string _executable;
    private readonly bool _allowUntrustedCerts;
    private readonly string? _caBundlePath;
    private readonly string? _opensslConfPath;
    private readonly AppLogger? _logger;
    private readonly string? _ffmpegPath;
    private readonly string? _tlsPluginDirectory;

    public YtDlpRunner(string executable, bool allowUntrustedCerts = false, string? caBundlePath = null, string? opensslConfPath = null, AppLogger? logger = null, string? ffmpegPath = null, string? tlsPluginDirectory = null)
    {
        _executable = executable;
        _ffmpegPath = ffmpegPath;
        _allowUntrustedCerts = allowUntrustedCerts;
        _caBundlePath = caBundlePath;
        _opensslConfPath = opensslConfPath;
        _logger = logger;
        _tlsPluginDirectory = tlsPluginDirectory;
    }

    /// <summary>
    /// v1.5.0: everything AllowUntrustedCertificates adds to a yt-dlp command line, shared
    /// by ALL three invocations (metadata, download, subtitles).
    ///
    ///   --no-check-certificates  verify_mode=CERT_NONE. Measured against a local server
    ///                            presenting a legacy-key-size RSA leaf: without it the official
    ///                            build fails with "EE certificate key too weak" (E-SSL02);
    ///                            with it the handshake succeeds.
    ///   --plugin-dirs DIR        loads YtDlpTlsPlugin, which also lowers SECLEVEL and
    ///                            disables verification on SSLContexts that the CLI flag
    ///                            does not reach. It used to be compiled into our patched
    ///                            yt-dlp.exe; see YtDlpTlsPlugin for why it no longer is.
    ///
    /// Up to v1.4.0 the download path never passed --no-check-certificates; it only worked
    /// behind inspecting proxies because the patched exe disabled verification itself.
    ///
    /// --plugin-dirs is emitted only for a directory that exists: yt-dlp exits with
    /// "Invalid plugin directory" otherwise, which would turn a failed plugin write into
    /// a dead app.
    /// </summary>
    private IEnumerable<string> BuildTlsRelaxArgs()
    {
        if (!_allowUntrustedCerts) yield break;
        yield return "--no-check-certificates";
        if (!string.IsNullOrEmpty(_tlsPluginDirectory) && Directory.Exists(_tlsPluginDirectory))
        {
            yield return "--plugin-dirs";
            yield return _tlsPluginDirectory;
        }
    }

    /// <summary>
    /// Measured cost of one yt-dlp cold start on THIS machine, set by AppHost from the
    /// startup warm-up. Added to every operation timeout.
    ///
    /// v1.3.8. The 30s metadata budget was written as "time for yt-dlp to do its job",
    /// but a PyInstaller onefile binary re-extracts its whole ~30 MB bundle into a
    /// brand-new %TEMP%\_MEIxxxxxx on EVERY run — warming the OS/AV caches helps, but
    /// nothing carries over between processes. Field log 2026-08-28 (UHD 710):
    /// `warmup_ms=24124`, and then a *subsequent* metadata call still reported
    /// `first_output_ms=18551`. So ~18-24s of the 30s was spent before yt-dlp did any
    /// work at all, leaving 6-12s for the network. That is enough on a plain network
    /// and not enough behind an HTTPS-inspecting proxy — which is exactly what the
    /// reporter observed: the same build works on non-inspected segments.
    ///
    /// Scaling the budget by what we actually measured beats guessing a bigger
    /// constant: fast machines keep their tight timeout, slow ones get the headroom
    /// they demonstrably need.
    /// </summary>
    public TimeSpan ObservedColdStart { get; set; } = TimeSpan.Zero;

    private TimeSpan Budget(TimeSpan work)
    {
        // Cap the added headroom so a pathological warm-up measurement can't turn an
        // interactive action into an unbounded wait.
        var coldStart = ObservedColdStart > MaxColdStartAllowance ? MaxColdStartAllowance : ObservedColdStart;
        return work + coldStart;
    }

    private static readonly TimeSpan MaxColdStartAllowance = TimeSpan.FromSeconds(60);

    // v1.1.19: structured invocation log. Distinguishes "Python startup hung"
    // (no first-output ms, no bytes) from "network call hung" (some bytes,
    // first-output ms < 1s) from "fragment retry loop" (lots of bytes,
    // first-output ms < 1s, timed_out=true). Hashes URLs the way AppLogger
    // does elsewhere — no plain video URLs ever land in the log file.
    // v1.1.25: flags that previously had to live in the user's
    // %APPDATA%\yt-dlp\config.txt for the app to work on their managed-network
    // setup. Baking them into every invocation eliminates the "tool depends
    // on hidden global config" failure mode that would catch every new user.
    //
    //   --force-ipv4         Prefer IPv4; some managed networks have broken
    //                        or blocked IPv6 and yt-dlp's getaddrinfo-then-try
    //                        loop hangs on the IPv6 attempt before falling back.
    //   --concurrent-fragments 8
    //                        DASH-fragmented downloads run 8 fragments in
    //                        parallel, dramatically faster on healthy networks
    //                        and tolerant of single-fragment retries.
    //   --continue           Resume partial downloads instead of starting over.
    //                        Combined with --part (yt-dlp default) this means
    //                        a flaky network drop only retries the affected
    //                        fragment, not the whole file.
    //   --throttled-rate 200K
    //                        If a fragment's speed drops below 200 KiB/s,
    //                        treat it as a stall and retry. On managed networks
    //                        with intermittent throttling this keeps the
    //                        download moving instead of waiting forever.
    //   --ignore-config      v1.5.0: and the other half of that failure mode — a
    //   --no-plugin-dirs     config.txt / yt-dlp.conf or plugin someone left for their
    //                        own CLI use is otherwise applied to every app call (the dev
    //                        machine has one). Everything the app needs is passed here;
    //                        BuildTlsRelaxArgs re-adds our own plugin folder AFTER the
    //                        clear, which is the order --no-plugin-dirs requires.
    private static IEnumerable<string> BuildCommonCliArgs()
    {
        yield return "--ignore-config";
        yield return "--no-plugin-dirs";
        yield return "--force-ipv4";
        yield return "--concurrent-fragments"; yield return "8";
        yield return "--continue";
        yield return "--throttled-rate";       yield return "200K";
    }

    private void LogInvokeBegin(string operation, IReadOnlyList<string> arguments, string? urlForHashing, IReadOnlyDictionary<string, string>? extraEnv)
    {
        if (_logger is null) return;
        _logger.Info("ytdlp.invoke.begin", new Dictionary<string, string>
        {
            ["op"]            = operation,
            ["arg_count"]     = arguments.Count.ToString(),
            ["url_hash"]      = urlForHashing is null ? "" : AppLogger.HashSuffix(urlForHashing),
            ["extra_env"]     = extraEnv is null ? "" : string.Join(',', extraEnv.Keys),
            // v1.2.4: field meaning changed from "did we add --no-check-certificates"
            // (legacy) to "did we relax HTTPS strictness via OPENSSL_CONF SECLEVEL=0"
            // (current). Both come from the same AllowUntrustedCertificates config
            // flag, so log continuity is preserved.
            ["allow_no_cert"] = _allowUntrustedCerts ? "true" : "false",
        });
    }

    private void LogInvokeEnd(string operation, ProcessExitInfo exit)
    {
        if (_logger is null) return;
        _logger.Info("ytdlp.invoke.end", new Dictionary<string, string>
        {
            ["op"]              = operation,
            ["exit_code"]       = exit.ExitCode.ToString(),
            ["timed_out"]       = exit.TimedOut ? "true" : "false",
            ["cancelled"]       = exit.Cancelled ? "true" : "false",
            ["pid"]             = exit.Pid.ToString(),
            ["start_ms"]        = exit.StartMs.ToString(),
            ["first_output_ms"] = exit.TimeToFirstOutputMs is { } ms ? ms.ToString() : "(none)",
            ["stdout_bytes"]    = exit.StdoutBytes.ToString(),
            ["stderr_bytes"]    = exit.StderrBytes.ToString(),
        });
    }

    // v1.1.17: explicit env-var injection for yt-dlp child processes. We
    // bundle SSL_CERT_FILE, REQUESTS_CA_BUNDLE, and CURL_CA_BUNDLE all
    // pointing at the same PEM file so whichever HTTP layer yt-dlp's
    // current release uses (urllib via certifi, requests, or urllib3)
    // sees the site-installed CA without any code knowing which one matters.
    private IReadOnlyDictionary<string, string>? BuildExtraEnv()
    {
        var hasCaBundle    = !string.IsNullOrEmpty(_caBundlePath) && File.Exists(_caBundlePath);
        var hasOpensslConf = _allowUntrustedCerts
            && !string.IsNullOrEmpty(_opensslConfPath)
            && File.Exists(_opensslConfPath);

        if (!hasCaBundle && !hasOpensslConf) return null;

        var env = new Dictionary<string, string>();
        if (hasCaBundle)
        {
            env["SSL_CERT_FILE"]      = _caBundlePath!;
            env["REQUESTS_CA_BUNDLE"] = _caBundlePath!;
            env["CURL_CA_BUNDLE"]     = _caBundlePath!;
        }
        // v1.2.4: OPENSSL_CONF env var. Set only when the user opted in to
        // AllowUntrustedCertificates. Python's bundled OpenSSL ignores this by
        // default (compiled with OPENSSL_INIT_NO_LOAD_CONFIG), so it's a no-op
        // on the upstream yt-dlp.exe. Kept as defence in depth for future Python
        // builds that might honour it.
        if (hasOpensslConf)
        {
            env["OPENSSL_CONF"] = _opensslConfPath!;
        }
        // v1.3.0, v1.5.0: the opt-in gate for the TLS plugin (YtDlpTlsPlugin). The
        // plugin is loaded via --plugin-dirs only when this setting is on, and on top
        // of that does nothing unless this variable is set — so a copy of the plugin
        // that yt-dlp somehow found on its own stays inert.
        if (_allowUntrustedCerts)
        {
            env["YTDLP_RELAX_SECLEVEL"] = "1";
        }
        return env;
    }

    public async Task<MetadataFetchResult> FetchMetadataAsync(
        string url,
        CancellationToken cancellationToken = default)
    {
        // v1.1.27: back to v1.1.16 pipe-based stdout capture (the managed-network
        // failure was IPv6 not stdout redirection; --force-ipv4 in
        // BuildCommonCliArgs is the actual fix). Layered on top:
        //   --write-thumbnail + --output <tmpdir>/<id>  → yt-dlp writes the
        //     thumbnail jpg/webp next to the same temp dir we control. The
        //     image is then copied to a persistent cache directory and a
        //     file:// URI substituted into VideoMetadata.ThumbnailUrl. The
        //     UI loader recognises file:// and reads from disk, avoiding an
        //     HttpClient call to i.ytimg.com that the user's environment was
        //     blocking.
        var thumbDir = Path.Combine(Path.GetTempPath(), "ytdlptool-meta-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(thumbDir);
        var outputTemplate = Path.Combine(thumbDir, "%(id)s");

        try
        {
            var fetchArgs = new List<string>
            {
                "--dump-single-json",
                // v1.1.28: -j (--dump-single-json) implies --simulate, which is
                // "do not write anything to disk" — so --write-thumbnail below
                // gets silently ignored. --no-simulate undoes that so the
                // thumbnail file actually lands in <outputTemplate>.<ext>.
                // --skip-download still suppresses the media download.
                "--no-simulate",
                "--skip-download",
                "--write-thumbnail",
                "--output", outputTemplate,
                "--no-playlist",
                "--no-warnings",
            };
            fetchArgs.AddRange(BuildCommonCliArgs());
            // v1.2.5: --no-check-certificates after v1.2.4's OPENSSL_CONF-only attempt
            // failed (OPENSSL_CONF is still set by BuildExtraEnv as a best-effort no-op).
            // v1.5.0: now comes from BuildTlsRelaxArgs together with the TLS plugin.
            fetchArgs.AddRange(BuildTlsRelaxArgs());
            AddSystemProxyArgs(fetchArgs);
            fetchArgs.Add("--");
            fetchArgs.Add(url);

            var extraEnv = BuildExtraEnv();
            var metadataTimeout = Budget(TimeSpan.FromSeconds(30));
            var args = new ProcessStartArguments(
                ExecutablePath: _executable,
                Arguments: fetchArgs,
                Timeout: metadataTimeout,
                ExtraEnv: extraEnv);

            LogInvokeBegin("metadata", fetchArgs, url, extraEnv);
            var stdoutLines = new List<string>();
            var exit = await ProcessSandbox.RunAsync(args,
                onStdout: line => stdoutLines.Add(line.Text),
                cancellationToken: cancellationToken);
            LogInvokeEnd("metadata", exit);

            if (exit.ExitCode != 0 || exit.TimedOut || exit.Cancelled)
            {
                var diag = BuildDiagnostics(exit);
                // The marker text is a CONTRACT with ErrorMapper's E-TIMEOUT02 rule,
                // which matches ^\[timeout after \d+s\]$ to detect a zero-output kill.
                // Keep the shape; only the number varies now that the budget is scaled
                // by the machine's measured cold start.
                if (exit.TimedOut) diag = $"[timeout after {(int)metadataTimeout.TotalSeconds}s]\n" + diag;
                else if (exit.Cancelled) diag = "[cancelled]\n" + diag;
                return new MetadataFetchResult(false, null, diag);
            }

            var raw = string.Join('\n', stdoutLines);
            YtDlpMetadataDto? dto;
            try
            {
                dto = JsonSerializer.Deserialize(raw, YtDlpJsonContext.Default.YtDlpMetadataDto);
            }
            catch (JsonException)
            {
                return new MetadataFetchResult(false, null, "JSON parse failed");
            }

            if (dto is null || string.IsNullOrEmpty(dto.Id) || string.IsNullOrEmpty(dto.Title))
                return new MetadataFetchResult(false, null, "missing fields");

            var thumbLocal = TryCopyThumbnailToCache(thumbDir, dto.Id);
            return new MetadataFetchResult(true, MapToMetadata(dto, thumbLocal), null);
        }
        finally
        {
            try { Directory.Delete(thumbDir, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>The most videos one playlist offers; yt-dlp stops paging after this many.</summary>
    public const int MaxPlaylistEntries = 500;

    // Placeholders YouTube keeps in a playlist for videos nobody can watch any more.
    private static readonly HashSet<string> UnavailableTitles =
        new(StringComparer.OrdinalIgnoreCase) { "[Private video]", "[Deleted video]", "[Unavailable video]" };

    private static readonly HashSet<string> UnavailableStates =
        new(StringComparer.OrdinalIgnoreCase) { "private", "needs_auth", "subscriber_only", "premium_only" };

    /// <summary>
    /// v1.5.0: the videos of a playlist without parsing each one: --flat-playlist reads
    /// the playlist page itself (about one request per 100 videos). Each video is parsed
    /// for real only when it is queued, so its formats are chosen exactly like a pasted link's.
    /// </summary>
    public async Task<PlaylistFetchResult> FetchPlaylistAsync(string url, CancellationToken cancellationToken = default)
    {
        var argList = new List<string>
        {
            "--flat-playlist",
            "--dump-single-json",
            "--yes-playlist",
            "--playlist-items", $"1:{MaxPlaylistEntries}",
            "--no-warnings",
        };
        argList.AddRange(BuildCommonCliArgs());
        argList.AddRange(BuildTlsRelaxArgs());
        AddSystemProxyArgs(argList);
        argList.Add("--");
        argList.Add(url);

        var extraEnv = BuildExtraEnv();
        var timeout = Budget(TimeSpan.FromSeconds(90));
        var args = new ProcessStartArguments(
            ExecutablePath: _executable,
            Arguments: argList,
            Timeout: timeout,
            ExtraEnv: extraEnv);

        LogInvokeBegin("playlist", argList, url, extraEnv);
        var stdoutLines = new List<string>();
        var exit = await ProcessSandbox.RunAsync(args,
            onStdout: line => stdoutLines.Add(line.Text),
            cancellationToken: cancellationToken);
        LogInvokeEnd("playlist", exit);

        if (exit.ExitCode != 0 || exit.TimedOut || exit.Cancelled)
        {
            var diag = BuildDiagnostics(exit);
            // Same markers as FetchMetadataAsync, which ErrorMapper understands.
            if (exit.TimedOut) diag = $"[timeout after {(int)timeout.TotalSeconds}s]\n" + diag;
            else if (exit.Cancelled) diag = "[cancelled]\n" + diag;
            return new PlaylistFetchResult(false, null, diag);
        }

        YtDlpPlaylistDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize(string.Join('\n', stdoutLines), YtDlpJsonContext.Default.YtDlpPlaylistDto);
        }
        catch (JsonException)
        {
            return new PlaylistFetchResult(false, null, "JSON parse failed");
        }
        // A Mix yt-dlp cannot expand comes back as the one video it was opened from.
        if (dto?.Entries is null) return new PlaylistFetchResult(false, null, "not a playlist", NotAPlaylist: true);

        var entries = dto.Entries
            // JSON null gaps, and entries that are not videos (a channel, a tab).
            .Where(e => e?.Id is { } id && VideoIdShape.IsMatch(id))
            .Select(e => new PlaylistEntry(
                VideoId: e!.Id!,
                Title: e.Title ?? e.Id!,
                Duration: e.Duration is > 0 and var s ? TimeSpan.FromSeconds(Math.Round(s)) : null,
                State: StateOf(e)))
            .ToList();
        return new PlaylistFetchResult(true, new PlaylistInfo(dto.Id ?? "", dto.Title ?? "", entries), null);
    }

    private static readonly System.Text.RegularExpressions.Regex VideoIdShape =
        new(@"^[A-Za-z0-9_-]{11}$", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static PlaylistEntryState StateOf(YtDlpPlaylistEntryDto e) =>
        e.LiveStatus switch
        {
            "is_live" => PlaylistEntryState.Live,
            "is_upcoming" => PlaylistEntryState.Upcoming,
            _ when UnavailableTitles.Contains(e.Title ?? "") || UnavailableStates.Contains(e.Availability ?? "")
                => PlaylistEntryState.Unavailable,
            _ => PlaylistEntryState.Available,
        };

    private static string? TryCopyThumbnailToCache(string sourceDir, string videoId)
    {
        try
        {
            var found = Directory.GetFiles(sourceDir)
                .Where(p =>
                {
                    var ext = Path.GetExtension(p).ToLowerInvariant();
                    return ext is ".jpg" or ".jpeg" or ".webp" or ".png";
                })
                .FirstOrDefault();
            if (found is null) return null;

            var cacheDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "YtDlpTool", "thumb-cache");
            Directory.CreateDirectory(cacheDir);

            // Strip any disallowed chars from videoId before using it in a path.
            var safeId = string.Concat(videoId.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_'));
            var ext2 = Path.GetExtension(found);
            var dst = Path.Combine(cacheDir, safeId + ext2);
            File.Copy(found, dst, overwrite: true);
            return new Uri(dst).AbsoluteUri;
        }
        catch
        {
            // Cache failure is non-fatal — UI just falls back to no thumbnail
            // (or the remote URL if our HttpClient happens to reach the CDN).
            return null;
        }
    }

    private static VideoMetadata MapToMetadata(YtDlpMetadataDto dto, string? localThumbnailUri = null)
    {
        var formats = (dto.Formats ?? Array.Empty<YtDlpFormatDto>())
            .Select(f => new VideoFormat(
                FormatId: f.FormatId ?? "",
                Height: f.Height,
                VideoCodec: f.Vcodec is null or "none" ? null : f.Vcodec,
                AudioCodec: f.Acodec is null or "none" ? null : f.Acodec,
                Extension: f.Ext ?? "",
                FileSizeBytes: f.Filesize ?? f.FilesizeApprox,
                AudioBitrateKbps: f.Abr.HasValue ? (int)Math.Round(f.Abr.Value) : null,
                Width: f.Width,
                DynamicRange: f.DynamicRange))
            .ToList();

        var subtitles = new List<SubtitleTrack>();
        if (dto.Subtitles is not null)
            foreach (var kv in dto.Subtitles)
                subtitles.Add(new SubtitleTrack(kv.Key, kv.Key, IsAutoGenerated: false));
        if (dto.AutomaticCaptions is not null)
            foreach (var kv in dto.AutomaticCaptions)
                if (!subtitles.Any(s => s.LanguageCode == kv.Key))
                    subtitles.Add(new SubtitleTrack(kv.Key, kv.Key + " (auto)", IsAutoGenerated: true));

        return new VideoMetadata(
            VideoId: dto.Id ?? "",
            Title: dto.Title ?? "",
            Channel: dto.Uploader ?? "",
            Duration: TimeSpan.FromSeconds(dto.Duration ?? 0),
            ThumbnailUrl: localThumbnailUri ?? dto.Thumbnail ?? "",
            Formats: formats,
            Subtitles: subtitles);
    }

    public async Task<DownloadResult> DownloadAsync(
        DownloadRequest request,
        IProgress<ProgressReport>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var argList = new List<string>();
        argList.AddRange(BuildFormatArgs(request));
        argList.AddRange(new[]
        {
            "--newline",
            "--progress-template",
            "[download] {\"percent\":%(progress._percent_str)s,\"speed\":\"%(progress._speed_str)s\",\"eta\":\"%(progress._eta_str)s\"}",
            "--no-playlist",
            // v1.1.25: retry counts bumped back up to 10 — the v1.1.8 reasoning
            // (silent retry loop > 90s watchdog) no longer applies because v1.1.23
            // dropped pipe-based output capture entirely, so retry verbosity has
            // no progress-parser to confuse. The user's pre-existing yt-dlp config
            // also uses 10, matching real-world managed-network reliability needs.
            "--retries", "10",
            "--fragment-retries", "10",
            "--retry-sleep", "3",
            // ARCHITECTURAL COMMITMENT (v1.1.11, refined v1.1.12): we deliberately
            // do NOT pass --extractor-args player_client=... here, even though it
            // was tempting to hard-code a client list for known PO Token /
            // JS-runtime fallbacks.
            //
            // Reasoning: yt-dlp's default client selection logic evolves with each
            // release to handle YouTube's changing PO Token / signature mechanism.
            // Hard-coding our own client list freezes us against whatever YouTube
            // did when the list was written; once YouTube tightens those specific
            // clients (as observed in v1.1.10 where all 6 we'd picked started
            // failing), the app stops working and ONLY a code change rescues it.
            //
            // By delegating to yt-dlp's defaults, "用戶端只需更新 yt-dlp 即可
            // 繼續使用" — the in-app 設定→進階→重新下載元件 button is sufficient
            // to recover from any future YouTube extractor change.
            //
            // v1.1.12 follow-up: clip extraction is NO LONGER yt-dlp's job. We do
            // a two-pass download (full file via yt-dlp, then trim via ffmpeg)
            // because yt-dlp's --download-sections requires a JavaScript runtime
            // (deno/node) to deobfuscate the section URL on current YouTube
            // videos — without one the section download hangs silently for
            // minutes while the full download path still works fine. ffmpeg
            // alone can stream-copy a sub-range losslessly, so the section
            // logic is removed here entirely and the executor sequences the
            // two passes itself.
            //
            // The only yt-dlp behaviour we own is: output template, progress
            // parsing, retry resilience, ffmpeg location, proxy injection,
            // subtitle/thumbnail toggles. Clip is ffmpeg's job. Everything
            // else is yt-dlp's call.
            "--output",
            BuildOutputTemplate(request),
        });
        argList.AddRange(BuildCommonCliArgs());
        // v1.1.13: subtitle download moved to DownloadSubtitlesOnlyAsync. The
        // media-only invocation NEVER passes --write-subs because doing so
        // changes the YouTube player_response path yt-dlp follows for the media
        // URL (heavier, JS-obfuscated, easily rate-limited). Subs land via a
        // separate yt-dlp --skip-download call and are muxed locally by ffmpeg.
        argList.AddRange(BuildFfmpegLocationArgs());
        // v1.5.0: this path was missing --no-check-certificates entirely (see
        // BuildTlsRelaxArgs).
        argList.AddRange(BuildTlsRelaxArgs());
        AddSystemProxyArgs(argList);
        if (request.EmbedThumbnail) argList.Add("--embed-thumbnail");
        if (request.ForceOverwrite) argList.Add("--force-overwrites");
        argList.Add("--");
        argList.Add(request.Url);

        var extraEnv = BuildExtraEnv();
        var args = new ProcessStartArguments(
            ExecutablePath: _executable,
            Arguments: argList,
            ExtraEnv: extraEnv);

        LogInvokeBegin("download", argList, request.Url, extraEnv);
        var (exit, finalPath, sawData) = await RunDownloadOnceAsync(args, progress, cancellationToken);
        LogInvokeEnd("download", exit);

        // v1.5.0: one automatic retry for a 403 that arrives after data was already
        // flowing. YouTube occasionally revokes a stream part-way through: field run
        // 2026-11-04, 1080p, "HTTP Error 403: Forbidden" at 7.8% after 20 MB at 9 MiB/s,
        // while the same download succeeded on six other attempts. yt-dlp does not retry
        // that 403 itself. A second run re-extracts fresh URLs and --continue resumes the
        // partial file — as long as the request has no ForceOverwrite: --force-overwrites
        // implies --no-continue, and until v1.5.0 the executor always set it, so this retry
        // (and every --throttled-rate re-extract) started again from byte 0. A 403 before
        // any data is a real refusal (age or region limits, a removed video) and is
        // reported as before.
        if (!exit.Cancelled && exit.ExitCode != 0 && sawData && IsForbidden(exit))
        {
            _logger?.Info("ytdlp.download.retry", new Dictionary<string, string>
            {
                ["reason"]   = "forbidden_mid_stream",
                ["url_hash"] = AppLogger.HashSuffix(request.Url),
            });
            LogInvokeBegin("download", argList, request.Url, extraEnv);
            (exit, finalPath, _) = await RunDownloadOnceAsync(args, progress, cancellationToken);
            LogInvokeEnd("download", exit);
        }

        if (exit.Cancelled) return new DownloadResult(false, null, BuildDiagnostics(exit), true);
        if (exit.ExitCode != 0) return new DownloadResult(false, null, BuildDiagnostics(exit), false);
        return new DownloadResult(true, finalPath, null, false);
    }

    private static async Task<(ProcessExitInfo Exit, string? FinalPath, bool SawData)> RunDownloadOnceAsync(
        ProcessStartArguments args, IProgress<ProgressReport>? progress, CancellationToken cancellationToken)
    {
        string? finalPath = null;
        var tracking = new DataTrackingProgress(progress);
        var exit = await ProcessSandbox.RunAsync(args,
            onStdout: line => ParseProgress(line.Text, tracking, ref finalPath),
            cancellationToken: cancellationToken);
        return (exit, finalPath, tracking.SawData);
    }

    private static bool IsForbidden(ProcessExitInfo exit) =>
        exit.Stderr?.Contains("HTTP Error 403", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>
    /// Forwards every report and notes whether any data arrived. Synchronous on purpose:
    /// the flag must be settled when the process exits, and Progress&lt;T&gt; would post it.
    /// </summary>
    private sealed class DataTrackingProgress(IProgress<ProgressReport>? inner) : IProgress<ProgressReport>
    {
        public bool SawData { get; private set; }

        public void Report(ProgressReport value)
        {
            if (value.Percent > 0) SawData = true;
            inner?.Report(value);
        }
    }

    /// <summary>
    /// Fix B (v1.1.8): builds a combined diagnostic blob from stderr + recent stdout.
    /// yt-dlp can hang or fail while writing nothing to stderr (e.g. silent retry
    /// loops, fragment retries with progress bars on stdout only). Including the
    /// stdout tail gives ErrorMapper and download.failed logs a useful payload even
    /// when stderr is empty. When stderr is empty we omit its [stderr] heading
    /// entirely so ErrorMapper's last-line fallback still hits the stdout content.
    /// </summary>
    private static string BuildDiagnostics(ProcessExitInfo exit)
    {
        var hasStderr = !string.IsNullOrWhiteSpace(exit.Stderr);
        var hasStdout = !string.IsNullOrWhiteSpace(exit.RecentStdout);
        if (!hasStderr && !hasStdout) return "";
        var sb = new System.Text.StringBuilder();
        if (hasStdout)
        {
            sb.Append("[stdout-tail]\n").Append(exit.RecentStdout);
            if (hasStderr) sb.Append('\n');
        }
        if (hasStderr)
        {
            sb.Append("[stderr]\n").Append(exit.Stderr);
        }
        return sb.ToString();
    }

    private static IEnumerable<string> BuildFormatArgs(DownloadRequest r)
    {
        return r.Mode switch
        {
            // v1.5.0: an MP3 is encoded at the bitrate its file name says (QualityOptions.Mp3Kbps).
            DownloadMode.AudioOnly when InferAudioFormat(r.ChosenFormat) == "mp3" =>
                new[] { "-f", r.ChosenFormat.FormatId, "-x", "--audio-format", "mp3",
                        "--audio-quality", $"{YtDlpTool.Domain.Services.QualityOptions.Mp3Kbps(r.ChosenFormat)}K" },
            DownloadMode.AudioOnly =>
                new[] { "-f", r.ChosenFormat.FormatId, "-x", "--audio-format", InferAudioFormat(r.ChosenFormat) },
            DownloadMode.VideoOnly =>
                new[] { "-f", r.ChosenFormat.FormatId },
            // v1.1.28: prefer m4a/AAC audio for the merged mp4. yt-dlp's default
            // `bestaudio` chooses by bitrate, which on YouTube tends to be Opus
            // (251) — but Opus in an mp4 container is non-standard and Windows
            // built-in Media Player won't play it (user feedback).
            // Format-selector preference list:
            //   <video>+bestaudio[ext=m4a]   ← AAC in m4a, the universally-played choice
            //   <video>+bestaudio[acodec=aac] ← any container with AAC
            //   <video>+bestaudio             ← give up and take best, ffmpeg will mux
            DownloadMode.AudioAndVideo =>
                new[]
                {
                    "-f", $"{r.ChosenFormat.FormatId}+bestaudio[ext=m4a]/" +
                          $"{r.ChosenFormat.FormatId}+bestaudio[acodec=aac]/" +
                          $"{r.ChosenFormat.FormatId}+bestaudio",
                    "--merge-output-format", "mp4",
                },
            _ => Array.Empty<string>()
        };
    }

    private static string InferAudioFormat(VideoFormat f) =>
        (f.Extension is "m4a" or "mp4") ? "m4a" : "mp3";

    /// <summary>
    /// v1.1.13: downloads subtitle sidecar files in a STANDALONE yt-dlp
    /// invocation (--skip-download). Bundling --write-subs with the media
    /// download triggers a heavier YouTube extractor path that returns
    /// JS-obfuscated media URLs; without a JavaScript runtime installed the
    /// media download then rate-limits and fails. Splitting the two phases
    /// keeps each invocation simple — one downloads media, the other downloads
    /// subs — and never the twain shall meet.
    ///
    /// Returns the list of .vtt/.srt files yt-dlp actually wrote to disk so
    /// the caller can feed them into ffmpeg-mux. On failure the method does
    /// not throw — it returns IsSuccess=false with a diagnostic blob; the
    /// caller can decide to continue without subs (best-effort) or surface
    /// the error.
    /// </summary>
    public async Task<SubtitleDownloadResult> DownloadSubtitlesOnlyAsync(
        string url,
        IReadOnlyList<string> languageCodes,
        string saveDirectory,
        string sanitizedFileStem,
        CancellationToken cancellationToken = default)
    {
        if (languageCodes.Count == 0)
            return new SubtitleDownloadResult(true, Array.Empty<string>(), null);

        var argList = new List<string>
        {
            "--skip-download",
            "--write-subs",
            "--write-auto-subs",
            "--sub-langs", string.Join(',', languageCodes),
            "--no-playlist",
            "--retries", "10",
            "--retry-sleep", "3",
            "--output", Path.Combine(saveDirectory, sanitizedFileStem + ".%(ext)s"),
        };
        argList.AddRange(BuildCommonCliArgs());
        argList.AddRange(BuildFfmpegLocationArgs());
        AddSystemProxyArgs(argList);
        argList.AddRange(BuildTlsRelaxArgs());
        argList.Add("--");
        argList.Add(url);

        var extraEnv = BuildExtraEnv();
        var args = new ProcessStartArguments(
            ExecutablePath: _executable,
            Arguments: argList,
            Timeout: TimeSpan.FromMinutes(2),
            ExtraEnv: extraEnv);

        LogInvokeBegin("subtitles", argList, url, extraEnv);
        var exit = await ProcessSandbox.RunAsync(args, cancellationToken: cancellationToken).ConfigureAwait(false);
        LogInvokeEnd("subtitles", exit);
        if (exit.ExitCode != 0)
            return new SubtitleDownloadResult(false, Array.Empty<string>(), BuildDiagnostics(exit));

        // Discover what yt-dlp actually wrote. yt-dlp may serve fewer (or different)
        // language tags than requested when a language has only auto-captions; rely
        // on the file system as the source of truth rather than echoing the input.
        var files = new List<string>();
        try
        {
            if (Directory.Exists(saveDirectory))
            {
                files.AddRange(Directory.GetFiles(saveDirectory, sanitizedFileStem + ".*.vtt"));
                files.AddRange(Directory.GetFiles(saveDirectory, sanitizedFileStem + ".*.srt"));
                files.Sort(StringComparer.Ordinal);
            }
        }
        catch
        {
            // best-effort discovery; treat as no files found
        }

        return new SubtitleDownloadResult(true, files, null);
    }

    /// <summary>
    /// Fix D: managed environments almost always have a corporate HTTP proxy configured
    /// in the Internet Settings hive. yt-dlp's urllib doesn't read WinHTTP, so we
    /// pass the detected proxy URL via --proxy here. No-op when no proxy is set.
    /// </summary>
    private static void AddSystemProxyArgs(List<string> argList)
    {
        var systemProxy = SystemProxy.DetectHttpProxy();
        if (!string.IsNullOrEmpty(systemProxy))
        {
            argList.Add("--proxy");
            argList.Add(systemProxy);
        }
    }

    /// <summary>
    /// Tell yt-dlp explicitly where to find ffmpeg.exe. We can't rely on the working
    /// directory or PATH because the app runs from a single-file extracted location that
    /// is NOT on PATH. Without this, clip extraction + audio re-mux can silently fail
    /// with "ffmpeg not found" inside the user's clip output.
    ///
    /// v1.4.0: the path is supplied by the caller. It used to be derived as a sibling of
    /// the yt-dlp executable, which held only while both lived in bin\. The --onedir
    /// yt-dlp lives in bin\yt-dlp\, so the derived path pointed at bin\yt-dlp\ffmpeg.exe,
    /// File.Exists said no, and this method yielded NOTHING — dropping the flag silently
    /// and breaking every merge/clip/audio path with "ffmpeg not found". Deriving one
    /// component's location from another's is the bug; pass it in.
    /// </summary>
    private IEnumerable<string> BuildFfmpegLocationArgs()
    {
        var ffmpegPath = _ffmpegPath;
        if (string.IsNullOrEmpty(ffmpegPath))
            ffmpegPath = Path.Combine(Path.GetDirectoryName(_executable) ?? "", "ffmpeg.exe");

        if (File.Exists(ffmpegPath))
        {
            yield return "--ffmpeg-location";
            yield return ffmpegPath;
        }
    }

    private static string BuildOutputTemplate(DownloadRequest r)
    {
        return Path.Combine(r.SaveDirectory, r.SanitizedFileStem + ".%(ext)s");
    }

    private static readonly System.Text.RegularExpressions.Regex DestinationRegex =
        new(@"\[download\]\s+Destination:\s*(?<path>.+)$", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex ProgressJsonRegex =
        new(@"\{""percent""\s*:\s*""?(?<pct>[\d.]+)%?""?[^}]*""speed""\s*:\s*""(?<speed>[^""]+)""[^}]*""eta""\s*:\s*""(?<eta>[^""]+)""\}",
            System.Text.RegularExpressions.RegexOptions.Compiled);

    // v1.5.0: what yt-dlp prints once the bytes are in — its post-processors and their
    // clean-up. The row then says 處理中 instead of the last download speed, frozen for the
    // 20 s and more that merging and embedding the cover take on a managed PC.
    private static readonly System.Text.RegularExpressions.Regex PostProcessorRegex = new(
        @"^(\[(Merger|EmbedThumbnail|ExtractAudio|Fixup\w*|ThumbnailsConvertor|Metadata|VideoRemuxer|VideoConvertor|EmbedSubtitle|ModifyChapters|SponsorBlock|MoveFiles)\]|Deleting original file )",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    private static void ParseProgress(string line, IProgress<ProgressReport>? progress, ref string? finalPath)
    {
        var destMatch = DestinationRegex.Match(line);
        if (destMatch.Success) finalPath = destMatch.Groups["path"].Value.Trim();

        if (progress is null) return;
        if (PostProcessorRegex.IsMatch(line))
        {
            progress.Report(new ProgressReport(100, null, null, Processing: true));
            return;
        }
        var m = ProgressJsonRegex.Match(line);
        if (!m.Success) return;
        if (!double.TryParse(m.Groups["pct"].Value, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var pct)) return;
        var bps = ParseSpeed(m.Groups["speed"].Value);
        var eta = ParseEta(m.Groups["eta"].Value);
        progress.Report(new ProgressReport(pct, bps, eta));
    }

    private static long? ParseSpeed(string s)
    {
        // "5.2MiB/s" / "512KiB/s" / "Unknown"
        var match = System.Text.RegularExpressions.Regex.Match(s,
            @"(?<v>[\d.]+)\s*(?<u>[KMGT]?i?B)/s", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!match.Success) return null;
        if (!double.TryParse(match.Groups["v"].Value, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var v)) return null;
        var unit = match.Groups["u"].Value.ToUpperInvariant();
        return (long)(v * unit switch
        {
            "KIB" => 1024.0, "KB" => 1000.0,
            "MIB" => 1024.0 * 1024, "MB" => 1_000_000.0,
            "GIB" => 1024.0 * 1024 * 1024, "GB" => 1_000_000_000.0,
            _ => 1.0
        });
    }

    private static TimeSpan? ParseEta(string s)
    {
        if (TimeSpan.TryParse(s, out var t)) return t;
        return null;
    }
}

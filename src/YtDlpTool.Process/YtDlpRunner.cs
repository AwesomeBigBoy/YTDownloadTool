using System.Text.Json;
using YtDlpTool.Domain.Logging;
using YtDlpTool.Domain.Models;

namespace YtDlpTool.Process;

public sealed class YtDlpRunner
{
    private readonly string _executable;
    private readonly bool _allowUntrustedCerts;
    private readonly string? _caBundlePath;
    private readonly AppLogger? _logger;

    public YtDlpRunner(string executable, bool allowUntrustedCerts = false, string? caBundlePath = null, AppLogger? logger = null)
    {
        _executable = executable;
        _allowUntrustedCerts = allowUntrustedCerts;
        _caBundlePath = caBundlePath;
        _logger = logger;
    }

    // v1.1.19: structured invocation log. Distinguishes "Python startup hung"
    // (no first-output ms, no bytes) from "network call hung" (some bytes,
    // first-output ms < 1s) from "fragment retry loop" (lots of bytes,
    // first-output ms < 1s, timed_out=true). Hashes URLs the way AppLogger
    // does elsewhere — no plain video URLs ever land in the log file.
    private void LogInvokeBegin(string operation, IReadOnlyList<string> arguments, string? urlForHashing, IReadOnlyDictionary<string, string>? extraEnv)
    {
        if (_logger is null) return;
        _logger.Info("ytdlp.invoke.begin", new Dictionary<string, string>
        {
            ["op"]            = operation,
            ["arg_count"]     = arguments.Count.ToString(),
            ["url_hash"]      = urlForHashing is null ? "" : AppLogger.HashSuffix(urlForHashing),
            ["extra_env"]     = extraEnv is null ? "" : string.Join(',', extraEnv.Keys),
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
        if (string.IsNullOrEmpty(_caBundlePath) || !File.Exists(_caBundlePath)) return null;
        return new Dictionary<string, string>
        {
            ["SSL_CERT_FILE"] = _caBundlePath,
            ["REQUESTS_CA_BUNDLE"] = _caBundlePath,
            ["CURL_CA_BUNDLE"] = _caBundlePath,
        };
    }

    public async Task<MetadataFetchResult> FetchMetadataAsync(
        string url,
        CancellationToken cancellationToken = default)
    {
        // v1.1.23: TTY mode — see ProcessStartArguments.NoIoRedirection. yt-dlp
        // writes the metadata JSON to a file instead of stdout, because endpoint security software
        // endpoint security software on managed hosts drops the application-layer payload for
        // processes with redirected stdout (heuristic: "headless = malware-like").
        // Direct CMD invocation works because cmd gives yt-dlp a real TTY.
        var infoDir = Path.Combine(Path.GetTempPath(), "ytdlptool-meta-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(infoDir);
        var outputTemplate = Path.Combine(infoDir, "%(id)s");

        try
        {
            var fetchArgs = new List<string>
            {
                "--skip-download",
                "--write-info-json",
                "--no-playlist",
                "--no-warnings",
                "--output", outputTemplate,
            };
            if (_allowUntrustedCerts) fetchArgs.Add("--no-check-certificates");
            AddSystemProxyArgs(fetchArgs);
            fetchArgs.Add("--");
            fetchArgs.Add(url);

            var extraEnv = BuildExtraEnv();
            var args = new ProcessStartArguments(
                ExecutablePath: _executable,
                Arguments: fetchArgs,
                // Bumped from 30s — without stdout we can't see early-output ms,
                // and the visible console adds a small startup cost.
                Timeout: TimeSpan.FromSeconds(60),
                ExtraEnv: extraEnv,
                NoIoRedirection: true);

            LogInvokeBegin("metadata", fetchArgs, url, extraEnv);
            var exit = await ProcessSandbox.RunAsync(args, cancellationToken: cancellationToken);
            LogInvokeEnd("metadata", exit);

            // In TTY mode we can't read stdout/stderr — the only signal of
            // success is the .info.json file appearing on disk.
            var jsonFiles = Directory.GetFiles(infoDir, "*.info.json");
            if (jsonFiles.Length == 0)
            {
                var marker = exit.TimedOut ? "[timeout after 60s]" :
                             exit.Cancelled ? "[cancelled]" :
                             $"[exit {exit.ExitCode}]";
                return new MetadataFetchResult(false, null, marker + " no .info.json produced — yt-dlp likely blocked or failed silently");
            }

            string raw;
            try
            {
                raw = await File.ReadAllTextAsync(jsonFiles[0], cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return new MetadataFetchResult(false, null, "failed to read .info.json: " + ex.Message);
            }

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

            return new MetadataFetchResult(true, MapToMetadata(dto), null);
        }
        finally
        {
            try { Directory.Delete(infoDir, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    private static VideoMetadata MapToMetadata(YtDlpMetadataDto dto)
    {
        var formats = (dto.Formats ?? Array.Empty<YtDlpFormatDto>())
            .Select(f => new VideoFormat(
                FormatId: f.FormatId ?? "",
                Height: f.Height,
                VideoCodec: f.Vcodec is null or "none" ? null : f.Vcodec,
                AudioCodec: f.Acodec is null or "none" ? null : f.Acodec,
                Extension: f.Ext ?? "",
                FileSizeBytes: f.Filesize ?? f.FilesizeApprox,
                AudioBitrateKbps: f.Abr.HasValue ? (int)Math.Round(f.Abr.Value) : null))
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
            ThumbnailUrl: dto.Thumbnail ?? "",
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
            // Fix B (v1.1.8): KEEP warnings on the download path. yt-dlp's WARNING
            // lines surface the actual reason a clip download stalls (e.g. "Unable
            // to extract DASH manifest, falling back to single stream") and feed
            // ProcessSandbox.RecentStdout so bug reports actually have a
            // chain of evidence. The metadata path still uses --no-warnings because
            // its stdout is parsed as JSON.
            //
            // Retries dropped from 10 to 3 and retry-sleep from 5s to 3s. With the
            // old 10x5s the silent retry loop alone could exceed the 90s watchdog;
            // 3 retries x 3s = 9s max silence per network blip, with warnings now
            // emitting during each retry so the watchdog clock resets.
            "--retries", "3",
            "--fragment-retries", "3",
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
        // v1.1.13: subtitle download moved to DownloadSubtitlesOnlyAsync. The
        // media-only invocation NEVER passes --write-subs because doing so
        // changes the YouTube player_response path yt-dlp follows for the media
        // URL (heavier, JS-obfuscated, easily rate-limited). Subs land via a
        // separate yt-dlp --skip-download call and are muxed locally by ffmpeg.
        argList.AddRange(BuildFfmpegLocationArgs());
        AddSystemProxyArgs(argList);
        if (request.EmbedThumbnail) argList.Add("--embed-thumbnail");
        if (request.ForceOverwrite) argList.Add("--force-overwrites");
        argList.Add("--");
        argList.Add(request.Url);

        var extraEnv = BuildExtraEnv();
        var args = new ProcessStartArguments(
            ExecutablePath: _executable,
            Arguments: argList,
            ExtraEnv: extraEnv,
            NoIoRedirection: true);

        LogInvokeBegin("download", argList, request.Url, extraEnv);
        var exit = await ProcessSandbox.RunAsync(args, cancellationToken: cancellationToken);
        LogInvokeEnd("download", exit);

        // v1.1.23: TTY mode means we cannot parse `[download]` progress lines
        // from stdout. The pre-determined output template tells us where
        // yt-dlp wrote the final file: scan the save directory for matches.
        // Filter: the final media file is `<stem>.<single-ext>` — anything with
        // more dots in the suffix (e.g. `.info.json`, `.en.vtt`, `.mp4.args`)
        // or known yt-dlp scratch extensions (.part, .ytdl) is sidecar / temp,
        // not the file we just produced for the user.
        string? finalPath = null;
        try
        {
            if (Directory.Exists(request.SaveDirectory))
            {
                var stem = request.SanitizedFileStem;
                finalPath = Directory.GetFiles(request.SaveDirectory, stem + ".*")
                    .FirstOrDefault(p =>
                    {
                        var name = Path.GetFileName(p);
                        if (!name.StartsWith(stem + ".", StringComparison.OrdinalIgnoreCase)) return false;
                        var suffix = name.Substring(stem.Length + 1);
                        if (suffix.Contains('.')) return false; // .info.json, .en.vtt, .mp4.args
                        if (suffix.Equals("part", StringComparison.OrdinalIgnoreCase)) return false;
                        if (suffix.Equals("ytdl", StringComparison.OrdinalIgnoreCase)) return false;
                        return true;
                    });
            }
        }
        catch { /* best-effort */ }

        if (exit.Cancelled) return new DownloadResult(false, null, "[cancelled]", true);
        if (exit.ExitCode != 0)
        {
            var marker = exit.TimedOut ? "[timeout]" : $"[exit {exit.ExitCode}]";
            return new DownloadResult(false, null, marker + " — yt-dlp ran in TTY mode; check the console flash for diagnostics or look for *.info.json in TEMP", false);
        }
        if (finalPath is null)
            return new DownloadResult(false, null, "yt-dlp exited cleanly but produced no output file (likely deleted by AV between completion and our scan)", false);

        return new DownloadResult(true, finalPath, null, false);
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
            DownloadMode.AudioOnly =>
                new[] { "-f", r.ChosenFormat.FormatId, "-x", "--audio-format", InferAudioFormat(r.ChosenFormat) },
            DownloadMode.VideoOnly =>
                new[] { "-f", r.ChosenFormat.FormatId },
            DownloadMode.AudioAndVideo =>
                new[] { "-f", $"{r.ChosenFormat.FormatId}+bestaudio", "--merge-output-format", "mp4" },
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
            "--retries", "3",
            "--retry-sleep", "3",
            "--output", Path.Combine(saveDirectory, sanitizedFileStem + ".%(ext)s"),
        };
        argList.AddRange(BuildFfmpegLocationArgs());
        AddSystemProxyArgs(argList);
        if (_allowUntrustedCerts) argList.Add("--no-check-certificates");
        argList.Add("--");
        argList.Add(url);

        var extraEnv = BuildExtraEnv();
        var args = new ProcessStartArguments(
            ExecutablePath: _executable,
            Arguments: argList,
            Timeout: TimeSpan.FromMinutes(2),
            ExtraEnv: extraEnv,
            NoIoRedirection: true);

        LogInvokeBegin("subtitles", argList, url, extraEnv);
        var exit = await ProcessSandbox.RunAsync(args, cancellationToken: cancellationToken).ConfigureAwait(false);
        LogInvokeEnd("subtitles", exit);
        if (exit.ExitCode != 0)
        {
            var marker = exit.TimedOut ? "[timeout]" : exit.Cancelled ? "[cancelled]" : $"[exit {exit.ExitCode}]";
            return new SubtitleDownloadResult(false, Array.Empty<string>(), marker);
        }

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
    /// </summary>
    private IEnumerable<string> BuildFfmpegLocationArgs()
    {
        var ffmpegPath = Path.Combine(Path.GetDirectoryName(_executable) ?? "", "ffmpeg.exe");
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

    private static void ParseProgress(string line, IProgress<ProgressReport>? progress, ref string? finalPath)
    {
        var destMatch = DestinationRegex.Match(line);
        if (destMatch.Success) finalPath = destMatch.Groups["path"].Value.Trim();

        if (progress is null) return;
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

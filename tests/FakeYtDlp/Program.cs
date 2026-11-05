using System.Text.Json;

var argList = args.ToList();

if (argList.Contains("--simulate-error"))
{
    var idx = argList.IndexOf("--simulate-error");
    var code = argList[idx + 1];
    Console.Error.WriteLine(code switch
    {
        "403" => "ERROR: unable to download video data: HTTP Error 403: Forbidden",
        "429" => "ERROR: HTTP Error 429: Too Many Requests",
        "unavailable" => "ERROR: [youtube] xxxxx: Video unavailable",
        "private" => "ERROR: [youtube] xxxxx: This video is private.",
        "network" => "ERROR: unable to download webpage: <urlopen error timed out>",
        _ => "ERROR: something went wrong"
    });
    return 1;
}

// Shared metadata payload used by both the legacy --dump-single-json path
// (stdout) and the v1.1.23 --write-info-json path (file).
object BuildMetadata() => new
{
    id = "FAKE0001234",
    title = "Fake Test Video",
    uploader = "Fake Channel",
    duration = 300,
    thumbnail = "https://i.ytimg.com/vi/FAKE0001234/hqdefault.jpg",
    formats = new object[]
    {
        new { format_id = "140", ext = "m4a", acodec = "mp4a", vcodec = "none", abr = 128, filesize = 4_800_000 },
        new { format_id = "251", ext = "webm", acodec = "opus", vcodec = "none", abr = 160, filesize = 6_000_000 },
        new { format_id = "299", ext = "mp4", vcodec = "avc1", acodec = "none", height = 1080, width = 1920, filesize = 120_000_000, dynamic_range = "SDR" },
        new { format_id = "337", ext = "webm", vcodec = "vp09.02.51.10.01.09.16.09.00", acodec = "none", height = 2160, width = 3840, filesize = 900_000_000, dynamic_range = "HDR10" },
        new { format_id = "sb0", ext = "mhtml", vcodec = "none", acodec = "none", height = 90, width = 160 },
        new { format_id = "298", ext = "mp4", vcodec = "avc1", acodec = "none", height = 720,  filesize = 80_000_000 },
        new { format_id = "135", ext = "mp4", vcodec = "avc1", acodec = "none", height = 480,  filesize = 40_000_000 }
    },
    subtitles = new Dictionary<string, object[]>
    {
        ["en"] = new object[] { new { ext = "vtt", url = "https://example/en.vtt" } },
        ["zh-TW"] = new object[] { new { ext = "vtt", url = "https://example/zh-TW.vtt" } }
    },
    automatic_captions = new Dictionary<string, object[]>
    {
        ["ja"] = new object[] { new { ext = "vtt", url = "https://example/auto.ja.vtt" } }
    }
};

// v1.5.0: a playlist read with --flat-playlist (checked before --dump-single-json, which
// the same call also passes). PLAYLISTERROR in the URL simulates a missing playlist.
if (argList.Contains("--flat-playlist"))
{
    if (argList[^1].Contains("PLAYLISTERROR"))
    {
        Console.Error.WriteLine("ERROR: [youtube:tab] PLAYLISTERROR: The playlist does not exist.");
        return 1;
    }
    // A Mix whose watch page yt-dlp resolves to the single video: no entries at all.
    if (argList[^1].Contains("NOTAPLAYLIST"))
    {
        Console.WriteLine(JsonSerializer.Serialize(new { _type = "video", id = "HHHHHHHHHHH", title = "Just a video" }));
        return 0;
    }
    // A gap, a channel entry, a live stream, an upcoming premiere, one ordinary video.
    if (argList[^1].Contains("PLFAKEMIXED"))
    {
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            _type = "playlist",
            id = "PLFAKEMIXED",
            title = "Mixed",
            entries = new object?[]
            {
                null,
                new { _type = "url", id = "UCuAXFkgsw1L7xaCfnd5JJOw", title = "A channel", live_status = (string?)null },
                new { _type = "url", id = "EEEEEEEEEEE", title = "Live now", live_status = (string?)"is_live" },
                new { _type = "url", id = "FFFFFFFFFFF", title = "Soon", live_status = (string?)"is_upcoming" },
                new { _type = "url", id = "GGGGGGGGGGG", title = "Normal", live_status = (string?)"not_live" },
            },
        }));
        return 0;
    }
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        _type = "playlist",
        id = "PLFAKE00000001",
        title = "Fake Playlist",
        entries = new object[]
        {
            new { _type = "url", id = "AAAAAAAAAAA", title = "First", duration = (double?)61.0 },
            new { _type = "url", id = "BBBBBBBBBBB", title = "[Private video]", duration = (double?)null },
            new { _type = "url", id = "CCCCCCCCCCC", title = "Third", duration = (double?)3725.0 },
            new { _type = "url", id = "DDDDDDDDDDD", title = "[Deleted video]", duration = (double?)null },
        },
    }));
    return 0;
}

if (argList.Contains("--dump-single-json"))
{
    Console.WriteLine(JsonSerializer.Serialize(BuildMetadata()));
    return 0;
}

// v1.1.23 metadata path: --skip-download --write-info-json --output <template>.
// yt-dlp writes the metadata to <template-with-id-substituted>.info.json.
// The mode is distinguished from the subtitle path (which also uses
// --skip-download) by the presence of --write-info-json without --write-subs.
if (argList.Contains("--skip-download") && argList.Contains("--write-info-json") && !argList.Contains("--write-subs"))
{
    var metaOutIdx = argList.IndexOf("--output");
    var metaTemplate = metaOutIdx >= 0 ? argList[metaOutIdx + 1] : "fake-info";
    // Substitute %(id)s with the fake id. Real yt-dlp also strips trailing
    // %(ext)s if present; our template doesn't have that here.
    var resolved = metaTemplate.Replace("%(id)s", "FAKE0001234", StringComparison.OrdinalIgnoreCase);
    var jsonPath = resolved + ".info.json";
    var dir = Path.GetDirectoryName(jsonPath);
    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
    await File.WriteAllTextAsync(jsonPath, JsonSerializer.Serialize(BuildMetadata()));
    return 0;
}

// Simulated subtitle-only download (--skip-download + --write-subs). Writes
// one .vtt sidecar per requested language and drops a sibling .args log so
// tests can assert on the argv composition.
if (argList.Contains("--skip-download") && argList.Contains("--write-subs"))
{
    var subOutIdx = argList.IndexOf("--output");
    var subOutTemplate = subOutIdx >= 0 ? argList[subOutIdx + 1] : "fake-subs.%(ext)s";
    var langIdx = argList.IndexOf("--sub-langs");
    var langs = (langIdx >= 0 && langIdx + 1 < argList.Count)
        ? argList[langIdx + 1].Split(',', StringSplitOptions.RemoveEmptyEntries)
        : Array.Empty<string>();

    // yt-dlp's --output uses %(ext)s; for subs the ext is the subtitle file
    // extension. Mimic the actual naming pattern: <stem>.<lang>.vtt.
    // The template here will be "<dir>/<stem>.%(ext)s" — convert to
    // "<dir>/<stem>.<lang>.vtt" for each language.
    var stemPath = subOutTemplate.Replace(".%(ext)s", "", StringComparison.OrdinalIgnoreCase);
    var stemDir = Path.GetDirectoryName(stemPath) ?? ".";
    if (!Directory.Exists(stemDir)) Directory.CreateDirectory(stemDir);
    var writtenSubs = new List<string>();
    foreach (var lang in langs)
    {
        var path = stemPath + "." + lang + ".vtt";
        await File.WriteAllTextAsync(path,
            "WEBVTT\n\n00:00:00.000 --> 00:00:05.000\nFake subtitle for " + lang + "\n");
        writtenSubs.Add(path);
    }

    // Side-channel args log next to the first sub (or in stemDir if no langs).
    var argsLog = (writtenSubs.Count > 0 ? writtenSubs[0] : Path.Combine(stemDir, "fake-subs"))
        + ".args";
    await File.WriteAllLinesAsync(argsLog, args);
    return 0;
}

// Simulated download — emit progress-template style lines.
var outIdx = argList.IndexOf("--output");
var outputPath = outIdx >= 0 ? argList[outIdx + 1] : "fake-output.mp4";
var realOutputPath = outputPath
    .Replace("%(title)s", "Fake Test Video")
    .Replace("%(ext)s", "mp4");
Directory.CreateDirectory(Path.GetDirectoryName(realOutputPath)!);

// One line per download run, so tests can count retries.
await File.AppendAllTextAsync(
    Path.Combine(Path.GetDirectoryName(realOutputPath)!, "fake-download-runs.txt"), "run" + Environment.NewLine);

// HTTP 403 simulations, chosen by the video id in the URL (the last argument):
//   START403            refused before any data arrives, every time
//   MIDSTREAM403        refused at 30% on the first run only (marker file), then succeeds
//   ALWAYSMIDSTREAM403  refused at 30% on every run
var url = argList.Count > 0 ? argList[^1] : "";
const string Forbidden = "ERROR: unable to download video data: HTTP Error 403: Forbidden";
if (url.Contains("START403"))
{
    Console.WriteLine("[download] {\"percent\":0.0,\"speed\":\"Unknown B/s\",\"eta\":\"Unknown\"}");
    Console.Error.WriteLine(Forbidden);
    return 1;
}
if (url.Contains("MIDSTREAM403"))
{
    var marker = realOutputPath + ".403-once";
    if (url.Contains("ALWAYS") || !File.Exists(marker))
    {
        for (int p = 0; p <= 30; p += 10)
            Console.WriteLine($"[download] {{\"percent\":{p},\"speed\":\"9.3MiB/s\",\"eta\":\"00:00:20\"}}");
        await File.WriteAllTextAsync(marker, "");
        Console.Error.WriteLine(Forbidden);
        return 1;
    }
}

for (int p = 0; p <= 100; p += 10)
{
    Console.WriteLine($"[download] {{\"percent\":{p},\"speed\":\"5.2MiB/s\",\"eta\":\"00:00:{100 - p:D2}\"}}");
    await Task.Delay(10);
}

Directory.CreateDirectory(Path.GetDirectoryName(realOutputPath)!);
await File.WriteAllTextAsync(realOutputPath, "fake video bytes");

// What real yt-dlp prints after the bytes are in (post-processors), for 影片 mode.
if (args.Contains("--merge-output-format"))
{
    Console.WriteLine($"[Merger] Merging formats into \"{realOutputPath}\"");
    Console.WriteLine($"Deleting original file {realOutputPath}.f299.mp4 (pass -k to keep)");
    Console.WriteLine($"[EmbedThumbnail] mutagen: Adding thumbnail to \"{realOutputPath}\"");
}

// Side-channel for tests: drop a sibling .args file listing the args this fake received.
// Real yt-dlp does nothing of the sort, but the file lets tests assert on argv composition
// without us having to redirect stdout (which already carries progress lines).
var argsLogPath = realOutputPath + ".args";
await File.WriteAllLinesAsync(argsLogPath, args);

Console.WriteLine($"[download] Destination: {realOutputPath}");
return 0;

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

if (argList.Contains("--dump-single-json"))
{
    var meta = new
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
            new { format_id = "299", ext = "mp4", vcodec = "avc1", acodec = "none", height = 1080, filesize = 120_000_000 },
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
    Console.WriteLine(JsonSerializer.Serialize(meta));
    return 0;
}

// Simulated download — emit progress-template style lines.
var outIdx = argList.IndexOf("--output");
var outputPath = outIdx >= 0 ? argList[outIdx + 1] : "fake-output.mp4";
var realOutputPath = outputPath
    .Replace("%(title)s", "Fake Test Video")
    .Replace("%(ext)s", "mp4");

for (int p = 0; p <= 100; p += 10)
{
    Console.WriteLine($"[download] {{\"percent\":{p},\"speed\":\"5.2MiB/s\",\"eta\":\"00:00:{100 - p:D2}\"}}");
    await Task.Delay(10);
}

Directory.CreateDirectory(Path.GetDirectoryName(realOutputPath)!);
await File.WriteAllTextAsync(realOutputPath, "fake video bytes");

// Side-channel for tests: drop a sibling .args file listing the args this fake received.
// Real yt-dlp does nothing of the sort, but the file lets tests assert on argv composition
// without us having to redirect stdout (which already carries progress lines).
var argsLogPath = realOutputPath + ".args";
await File.WriteAllLinesAsync(argsLogPath, args);

Console.WriteLine($"[download] Destination: {realOutputPath}");
return 0;

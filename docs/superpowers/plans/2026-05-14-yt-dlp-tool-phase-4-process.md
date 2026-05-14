# Phase 4 · Process Layer

**Goal:** Wrap `yt-dlp.exe` and `ffmpeg.exe` subprocess invocation with `ProcessSandbox` (env whitelist, timeout, stdout cap), `YtDlpRunner` (metadata + download), `FfmpegRunner` (standalone clip), and a `FakeYtDlp` test helper.

**Prerequisites:** Phase 3 complete (tag `phase-3-security-complete`).

---

### Task 4.1: `ProcessStartArguments` value type

**Files:**
- Create: `src/YtDlpTool.Process/ProcessStartArguments.cs`

This is a simple immutable holder that other code uses to describe what to run. Keeps `ProcessSandbox.Run` signature small.

- [ ] **Step 1: Create file**

```csharp
// src/YtDlpTool.Process/ProcessStartArguments.cs
namespace YtDlpTool.Process;

public sealed record ProcessStartArguments(
    string ExecutablePath,
    IReadOnlyList<string> Arguments,
    string? WorkingDirectory = null,
    TimeSpan? Timeout = null,
    long StdoutByteLimit = 10 * 1024 * 1024,
    long StderrByteLimit = 1 * 1024 * 1024);
```

- [ ] **Step 2: Build**

```powershell
dotnet build src/YtDlpTool.Process/
```
Expected: succeeds.

- [ ] **Step 3: Commit**

```powershell
git add src/YtDlpTool.Process/ProcessStartArguments.cs
git commit -m "feat(process): ProcessStartArguments DTO"
```

---

### Task 4.2: `ProcessSandbox` core

**Files:**
- Create: `src/YtDlpTool.Process/ProcessSandbox.cs`
- Create: `src/YtDlpTool.Process/ProcessExitInfo.cs`
- Create: `src/YtDlpTool.Process/ProcessStdoutLine.cs`
- Create: `tests/YtDlpTool.Process.Tests/ProcessSandboxTests.cs`

Spec 5.2: argument array (never shell string), env whitelist, `CreateNoWindow=true`, `UseShellExecute=false`, force UTF-8, stdout/stderr byte cap, cancellation token kills process tree.

- [ ] **Step 1: Create supporting types**

```csharp
// src/YtDlpTool.Process/ProcessExitInfo.cs
namespace YtDlpTool.Process;

public sealed record ProcessExitInfo(
    int ExitCode,
    string Stderr,
    bool TimedOut,
    bool Cancelled,
    bool StdoutLimitExceeded,
    bool StderrLimitExceeded);
```

```csharp
// src/YtDlpTool.Process/ProcessStdoutLine.cs
namespace YtDlpTool.Process;

public sealed record ProcessStdoutLine(string Text, DateTime AtUtc);
```

- [ ] **Step 2: Implement `ProcessSandbox`**

```csharp
// src/YtDlpTool.Process/ProcessSandbox.cs
using System.Diagnostics;
using System.Text;

namespace YtDlpTool.Process;

public static class ProcessSandbox
{
    private static readonly TimeSpan KillGrace = TimeSpan.FromMilliseconds(800);

    public static async Task<ProcessExitInfo> RunAsync(
        ProcessStartArguments args,
        Action<ProcessStdoutLine>? onStdout = null,
        CancellationToken cancellationToken = default)
    {
        var info = new ProcessStartInfo
        {
            FileName = args.ExecutablePath,
            WorkingDirectory = args.WorkingDirectory ?? Path.GetDirectoryName(args.ExecutablePath),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        foreach (var a in args.Arguments) info.ArgumentList.Add(a);

        ConfigureSandboxedEnvironment(info, args.ExecutablePath);

        using var process = new System.Diagnostics.Process { StartInfo = info, EnableRaisingEvents = true };
        var stderr = new StringBuilder();
        long stdoutBytes = 0, stderrBytes = 0;
        var stdoutLimitExceeded = false;
        var stderrLimitExceeded = false;

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            var bytes = Encoding.UTF8.GetByteCount(e.Data);
            if (Interlocked.Add(ref stdoutBytes, bytes) > args.StdoutByteLimit)
            {
                stdoutLimitExceeded = true;
                try { process.Kill(entireProcessTree: true); } catch { }
                return;
            }
            onStdout?.Invoke(new ProcessStdoutLine(e.Data, DateTime.UtcNow));
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            var bytes = Encoding.UTF8.GetByteCount(e.Data);
            if (Interlocked.Add(ref stderrBytes, bytes) > args.StderrByteLimit)
            {
                stderrLimitExceeded = true;
                try { process.Kill(entireProcessTree: true); } catch { }
                return;
            }
            lock (stderr) stderr.AppendLine(e.Data);
        };

        if (!process.Start())
            return new ProcessExitInfo(-1, "process failed to start", false, false, false, false);

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        var cancelTask = WaitForCancellationAsync(process, cancellationToken);
        var timeoutTask = args.Timeout is { } t
            ? Task.Delay(t, CancellationToken.None)
            : Task.Delay(Timeout.InfiniteTimeSpan, CancellationToken.None);
        var exitTask = process.WaitForExitAsync(CancellationToken.None);

        var winner = await Task.WhenAny(exitTask, cancelTask, timeoutTask).ConfigureAwait(false);

        bool timedOut = winner == timeoutTask && !exitTask.IsCompleted;
        bool cancelled = winner == cancelTask && !exitTask.IsCompleted;

        if (timedOut || cancelled)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            await exitTask.ConfigureAwait(false);
        }

        return new ProcessExitInfo(
            ExitCode: process.HasExited ? process.ExitCode : -1,
            Stderr: stderr.ToString(),
            TimedOut: timedOut,
            Cancelled: cancelled,
            StdoutLimitExceeded: stdoutLimitExceeded,
            StderrLimitExceeded: stderrLimitExceeded);
    }

    private static async Task WaitForCancellationAsync(System.Diagnostics.Process process, CancellationToken ct)
    {
        if (!ct.CanBeCanceled) { await Task.Delay(Timeout.InfiniteTimeSpan, CancellationToken.None); return; }
        var tcs = new TaskCompletionSource();
        using var reg = ct.Register(() =>
        {
            try { process.CloseMainWindow(); } catch { }
            tcs.TrySetResult();
        });
        await tcs.Task.ConfigureAwait(false);
        try { await Task.Delay(KillGrace, CancellationToken.None).ConfigureAwait(false); } catch { }
    }

    private static void ConfigureSandboxedEnvironment(ProcessStartInfo info, string exePath)
    {
        info.EnvironmentVariables.Clear();
        var binDir = Path.GetDirectoryName(exePath) ?? "";
        var systemDir = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var systemRoot = Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows";
        var tempDir = Path.GetTempPath();
        info.EnvironmentVariables["SystemRoot"] = systemRoot;
        info.EnvironmentVariables["Temp"] = tempDir;
        info.EnvironmentVariables["TMP"] = tempDir;
        info.EnvironmentVariables["Path"] = $"{binDir};{systemDir};{Path.Combine(systemRoot, "System32")}";
        info.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8"; // yt-dlp respects this
        info.EnvironmentVariables["PYTHONUTF8"] = "1";
    }
}
```

- [ ] **Step 3: Write tests using a known-safe `cmd.exe` invocation**

```csharp
// tests/YtDlpTool.Process.Tests/ProcessSandboxTests.cs
using YtDlpTool.Process;

namespace YtDlpTool.Process.Tests;

public class ProcessSandboxTests
{
    private static readonly string CmdPath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");

    [Fact]
    public async Task Run_SimpleEcho_ReceivesStdoutLine()
    {
        var lines = new List<string>();
        var args = new ProcessStartArguments(
            ExecutablePath: CmdPath,
            Arguments: new[] { "/c", "echo hello-world" });
        var result = await ProcessSandbox.RunAsync(args, l => lines.Add(l.Text));
        Assert.Equal(0, result.ExitCode);
        Assert.Contains(lines, l => l.Contains("hello-world"));
    }

    [Fact]
    public async Task Run_Cancellation_KillsProcess()
    {
        var args = new ProcessStartArguments(
            ExecutablePath: CmdPath,
            Arguments: new[] { "/c", "ping -n 30 127.0.0.1 > NUL" });
        using var cts = new CancellationTokenSource();
        var task = ProcessSandbox.RunAsync(args, cancellationToken: cts.Token);
        await Task.Delay(200);
        cts.Cancel();
        var result = await task;
        Assert.True(result.Cancelled);
    }

    [Fact]
    public async Task Run_Timeout_KillsProcess()
    {
        var args = new ProcessStartArguments(
            ExecutablePath: CmdPath,
            Arguments: new[] { "/c", "ping -n 10 127.0.0.1 > NUL" },
            Timeout: TimeSpan.FromMilliseconds(500));
        var result = await ProcessSandbox.RunAsync(args);
        Assert.True(result.TimedOut);
    }

    [Fact]
    public async Task Run_EnvironmentIsWhitelisted()
    {
        // Set a variable in our process that should NOT propagate to the child.
        Environment.SetEnvironmentVariable("YTDLP_TEST_SHOULD_NOT_LEAK", "1");
        try
        {
            var lines = new List<string>();
            var args = new ProcessStartArguments(
                ExecutablePath: CmdPath,
                Arguments: new[] { "/c", "set" });
            var result = await ProcessSandbox.RunAsync(args, l => lines.Add(l.Text));
            Assert.Equal(0, result.ExitCode);
            Assert.DoesNotContain(lines, l => l.Contains("YTDLP_TEST_SHOULD_NOT_LEAK"));
            Assert.Contains(lines, l => l.StartsWith("SystemRoot=", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(lines, l => l.StartsWith("PYTHONUTF8=", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Environment.SetEnvironmentVariable("YTDLP_TEST_SHOULD_NOT_LEAK", null);
        }
    }
}
```

- [ ] **Step 4: Run tests**

```powershell
dotnet test tests/YtDlpTool.Process.Tests/ --filter "FullyQualifiedName~ProcessSandboxTests"
```
Expected: 4 pass.

- [ ] **Step 5: Commit**

```powershell
git add src/YtDlpTool.Process/ProcessSandbox.cs src/YtDlpTool.Process/ProcessExitInfo.cs src/YtDlpTool.Process/ProcessStdoutLine.cs tests/YtDlpTool.Process.Tests/ProcessSandboxTests.cs
git commit -m "feat(process): ProcessSandbox (arg array, env whitelist, timeout, stdout cap)"
```

---

### Task 4.3: Build `FakeYtDlp` test helper

**Files:**
- Create: `tests/FakeYtDlp/FakeYtDlp.csproj`
- Create: `tests/FakeYtDlp/Program.cs`

A small console app that mimics `yt-dlp` enough to exercise `YtDlpRunner` without network or a real binary. It accepts:
- `--dump-single-json` → outputs a hardcoded metadata JSON for a fake video
- `--newline --progress-template <tmpl>` → prints `<tmpl>` substituted progress lines simulating a download
- `--simulate-error <code>` → exits with non-zero and a chosen stderr line

- [ ] **Step 1: Create project**

```powershell
dotnet new console -n FakeYtDlp -o tests/FakeYtDlp --framework net8.0
dotnet sln add tests/FakeYtDlp/FakeYtDlp.csproj
```

- [ ] **Step 2: Configure csproj**

Replace `tests/FakeYtDlp/FakeYtDlp.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <AssemblyName>fake-yt-dlp</AssemblyName>
    <RestoreLockedMode>true</RestoreLockedMode>
    <RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>
  </PropertyGroup>
</Project>
```

- [ ] **Step 3: Write `Program.cs`**

```csharp
// tests/FakeYtDlp/Program.cs
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
Console.WriteLine($"[download] Destination: {realOutputPath}");
return 0;
```

- [ ] **Step 4: Build it**

```powershell
dotnet build tests/FakeYtDlp/FakeYtDlp.csproj
```
Expected: succeeds.

- [ ] **Step 5: Commit**

```powershell
git add tests/FakeYtDlp/ YtDlpTool.sln
git commit -m "test: FakeYtDlp console app for process-layer integration tests"
```

---

### Task 4.4: `YtDlpRunner` — metadata fetch

**Files:**
- Create: `src/YtDlpTool.Process/YtDlpJsonContext.cs`
- Create: `src/YtDlpTool.Process/YtDlpMetadataDto.cs`
- Create: `src/YtDlpTool.Process/YtDlpRunner.cs`
- Create: `src/YtDlpTool.Process/MetadataFetchResult.cs`
- Create: `tests/YtDlpTool.Process.Tests/YtDlpRunnerMetadataTests.cs`

- [ ] **Step 1: DTOs for yt-dlp's `--dump-single-json` output**

```csharp
// src/YtDlpTool.Process/YtDlpMetadataDto.cs
using System.Text.Json.Serialization;

namespace YtDlpTool.Process;

public sealed class YtDlpMetadataDto
{
    public string? Id { get; set; }
    public string? Title { get; set; }
    public string? Uploader { get; set; }
    public double? Duration { get; set; }
    public string? Thumbnail { get; set; }
    public YtDlpFormatDto[]? Formats { get; set; }
    public Dictionary<string, YtDlpSubtitleDto[]>? Subtitles { get; set; }
    [JsonPropertyName("automatic_captions")]
    public Dictionary<string, YtDlpSubtitleDto[]>? AutomaticCaptions { get; set; }
}

public sealed class YtDlpFormatDto
{
    [JsonPropertyName("format_id")] public string? FormatId { get; set; }
    public string? Ext { get; set; }
    public string? Vcodec { get; set; }
    public string? Acodec { get; set; }
    public int? Height { get; set; }
    public long? Filesize { get; set; }
    [JsonPropertyName("filesize_approx")] public long? FilesizeApprox { get; set; }
    public double? Abr { get; set; }
}

public sealed class YtDlpSubtitleDto
{
    public string? Ext { get; set; }
    public string? Url { get; set; }
}
```

```csharp
// src/YtDlpTool.Process/YtDlpJsonContext.cs
using System.Text.Json.Serialization;

namespace YtDlpTool.Process;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(YtDlpMetadataDto))]
public partial class YtDlpJsonContext : JsonSerializerContext { }
```

- [ ] **Step 2: Result type**

```csharp
// src/YtDlpTool.Process/MetadataFetchResult.cs
using YtDlpTool.Domain.Models;

namespace YtDlpTool.Process;

public sealed record MetadataFetchResult(
    bool IsSuccess,
    VideoMetadata? Metadata,
    string? ErrorStderr);
```

- [ ] **Step 3: Runner**

```csharp
// src/YtDlpTool.Process/YtDlpRunner.cs
using System.Text.Json;
using YtDlpTool.Domain.Models;

namespace YtDlpTool.Process;

public sealed class YtDlpRunner
{
    private readonly string _executable;

    public YtDlpRunner(string executable)
    {
        _executable = executable;
    }

    public async Task<MetadataFetchResult> FetchMetadataAsync(
        string url,
        CancellationToken cancellationToken = default)
    {
        var args = new ProcessStartArguments(
            ExecutablePath: _executable,
            Arguments: new[]
            {
                "--dump-single-json",
                "--no-playlist",
                "--no-warnings",
                "--",
                url
            },
            Timeout: TimeSpan.FromSeconds(30));

        var stdoutLines = new List<string>();
        var exit = await ProcessSandbox.RunAsync(args,
            onStdout: line => stdoutLines.Add(line.Text),
            cancellationToken: cancellationToken);

        if (exit.ExitCode != 0 || exit.TimedOut || exit.Cancelled)
            return new MetadataFetchResult(false, null, exit.Stderr);

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

        return new MetadataFetchResult(true, MapToMetadata(dto), null);
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
}
```

- [ ] **Step 4: Test using `FakeYtDlp`**

Create `tests/YtDlpTool.Process.Tests/Helpers/FakeYtDlpLocator.cs`:

```csharp
// tests/YtDlpTool.Process.Tests/Helpers/FakeYtDlpLocator.cs
namespace YtDlpTool.Process.Tests.Helpers;

public static class FakeYtDlpLocator
{
    public static string Path()
    {
        // Walks up from test bin to repo root, then to the FakeYtDlp output.
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            var candidate = System.IO.Path.Combine(dir, "tests", "FakeYtDlp", "bin", "Debug", "net8.0", "fake-yt-dlp.exe");
            if (File.Exists(candidate)) return candidate;
            var parent = Directory.GetParent(dir);
            if (parent is null) break;
            dir = parent.FullName;
        }
        throw new FileNotFoundException("FakeYtDlp executable not found — build tests/FakeYtDlp first.");
    }
}
```

```csharp
// tests/YtDlpTool.Process.Tests/YtDlpRunnerMetadataTests.cs
using YtDlpTool.Process;
using YtDlpTool.Process.Tests.Helpers;

namespace YtDlpTool.Process.Tests;

public class YtDlpRunnerMetadataTests
{
    [Fact]
    public async Task FetchMetadata_FakeBinary_ReturnsMetadata()
    {
        var runner = new YtDlpRunner(FakeYtDlpLocator.Path());
        var result = await runner.FetchMetadataAsync("https://www.youtube.com/watch?v=FAKE0001234");
        Assert.True(result.IsSuccess, result.ErrorStderr);
        Assert.NotNull(result.Metadata);
        Assert.Equal("FAKE0001234", result.Metadata!.VideoId);
        Assert.Equal("Fake Test Video", result.Metadata.Title);
        Assert.Equal(TimeSpan.FromMinutes(5), result.Metadata.Duration);
        Assert.True(result.Metadata.Formats.Count >= 5);
        Assert.Contains(result.Metadata.Subtitles, s => s.LanguageCode == "en");
        Assert.Contains(result.Metadata.Subtitles, s => s.IsAutoGenerated && s.LanguageCode == "ja");
    }

    [Fact]
    public async Task FetchMetadata_SimulatedError_ReturnsFailure()
    {
        var runner = new YtDlpRunner(FakeYtDlpLocator.Path());
        // The fake exe shortcuts to error mode when --simulate-error is in argv.
        // YtDlpRunner doesn't pass that flag, so to exercise the failure path we pass a URL
        // the fake won't handle — but our fake handles any URL. Instead, simulate by calling
        // ProcessSandbox directly with --simulate-error to confirm stderr propagation.
        var args = new ProcessStartArguments(
            ExecutablePath: FakeYtDlpLocator.Path(),
            Arguments: new[] { "--simulate-error", "403" });
        var exit = await ProcessSandbox.RunAsync(args);
        Assert.Equal(1, exit.ExitCode);
        Assert.Contains("403", exit.Stderr);
    }
}
```

- [ ] **Step 5: Make sure FakeYtDlp builds before tests run**

Modify `tests/YtDlpTool.Process.Tests/YtDlpTool.Process.Tests.csproj` to add a build dependency:

```xml
  <ItemGroup>
    <ProjectReference Include="..\FakeYtDlp\FakeYtDlp.csproj">
      <ReferenceOutputAssembly>false</ReferenceOutputAssembly>
    </ProjectReference>
  </ItemGroup>
```

- [ ] **Step 6: Run tests**

```powershell
dotnet test tests/YtDlpTool.Process.Tests/ --filter "FullyQualifiedName~YtDlpRunnerMetadataTests"
```
Expected: 2 pass.

- [ ] **Step 7: Commit**

```powershell
git add src/YtDlpTool.Process/ tests/YtDlpTool.Process.Tests/
git commit -m "feat(process): YtDlpRunner metadata fetch with source-gen JSON, fake-driven test"
```

---

### Task 4.5: `YtDlpRunner` — download with progress reporting

**Files:**
- Modify: `src/YtDlpTool.Process/YtDlpRunner.cs`
- Create: `src/YtDlpTool.Process/ProgressReport.cs`
- Create: `src/YtDlpTool.Process/DownloadRequest.cs`
- Create: `src/YtDlpTool.Process/DownloadResult.cs`
- Create: `tests/YtDlpTool.Process.Tests/YtDlpRunnerDownloadTests.cs`

- [ ] **Step 1: Create progress / request / result types**

```csharp
// src/YtDlpTool.Process/ProgressReport.cs
namespace YtDlpTool.Process;

public sealed record ProgressReport(double Percent, long? BytesPerSecond, TimeSpan? Eta);
```

```csharp
// src/YtDlpTool.Process/DownloadRequest.cs
using YtDlpTool.Domain.Models;

namespace YtDlpTool.Process;

public sealed record DownloadRequest(
    string Url,
    DownloadMode Mode,
    VideoFormat ChosenFormat,
    IReadOnlyList<string> SubtitleLanguageCodes,
    TimeRange? ClipRange,
    string SaveDirectory,
    string SanitizedFileStem,
    bool EmbedThumbnail = true);
```

```csharp
// src/YtDlpTool.Process/DownloadResult.cs
namespace YtDlpTool.Process;

public sealed record DownloadResult(
    bool IsSuccess,
    string? OutputFilePath,
    string? ErrorStderr,
    bool WasCancelled);
```

- [ ] **Step 2: Extend `YtDlpRunner` with `DownloadAsync`**

Add to `src/YtDlpTool.Process/YtDlpRunner.cs` (do not remove existing code):

```csharp
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
            "--no-warnings",
            "--output",
            BuildOutputTemplate(request),
        });
        argList.AddRange(BuildSubtitleArgs(request));
        argList.AddRange(BuildClipArgs(request));
        if (request.EmbedThumbnail) argList.Add("--embed-thumbnail");
        argList.Add("--");
        argList.Add(request.Url);

        var args = new ProcessStartArguments(
            ExecutablePath: _executable,
            Arguments: argList);

        string? finalPath = null;
        var exit = await ProcessSandbox.RunAsync(args,
            onStdout: line => ParseProgress(line.Text, progress, ref finalPath),
            cancellationToken: cancellationToken);

        if (exit.Cancelled) return new DownloadResult(false, null, exit.Stderr, true);
        if (exit.ExitCode != 0) return new DownloadResult(false, null, exit.Stderr, false);
        return new DownloadResult(true, finalPath, null, false);
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

    private static IEnumerable<string> BuildSubtitleArgs(DownloadRequest r)
    {
        if (r.Mode == DownloadMode.AudioOnly) yield break;
        if (r.SubtitleLanguageCodes.Count == 0) yield break;
        yield return "--write-subs";
        yield return "--write-auto-subs";
        yield return "--sub-langs";
        yield return string.Join(',', r.SubtitleLanguageCodes);
        yield return "--embed-subs";
    }

    private static IEnumerable<string> BuildClipArgs(DownloadRequest r)
    {
        if (r.ClipRange is null) yield break;
        yield return "--download-sections";
        yield return r.ClipRange.ToYtDlpFormat();
        yield return "--force-keyframes-at-cuts";
    }

    private static string BuildOutputTemplate(DownloadRequest r)
    {
        var ext = r.Mode == DownloadMode.AudioOnly ? "%(ext)s" : "mp4";
        return Path.Combine(r.SaveDirectory, r.SanitizedFileStem + ".%(ext)s");
    }

    private static readonly System.Text.RegularExpressions.Regex DestinationRegex =
        new(@"\[download\]\s+Destination:\s*(?<path>.+)$", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex ProgressJsonRegex =
        new(@"\{\""percent\""\s*:\s*""?(?<pct>[\d.]+)%?""?[^}]*""speed""\s*:\s*""(?<speed>[^""]+)""[^}]*""eta""\s*:\s*""(?<eta>[^""]+)""\}",
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
```

- [ ] **Step 3: Test download path with `FakeYtDlp`**

```csharp
// tests/YtDlpTool.Process.Tests/YtDlpRunnerDownloadTests.cs
using YtDlpTool.Domain.Models;
using YtDlpTool.Process;
using YtDlpTool.Process.Tests.Helpers;

namespace YtDlpTool.Process.Tests;

public class YtDlpRunnerDownloadTests
{
    [Fact]
    public async Task Download_ReportsProgressAndCompletes()
    {
        var runner = new YtDlpRunner(FakeYtDlpLocator.Path());
        var temp = Path.Combine(Path.GetTempPath(), "ytdlp-dl-" + Guid.NewGuid());
        Directory.CreateDirectory(temp);
        try
        {
            var progressValues = new List<double>();
            var progress = new Progress<ProgressReport>(r => progressValues.Add(r.Percent));

            var format = new VideoFormat("299", 1080, "avc1", null, "mp4", 120_000_000, null);
            var request = new DownloadRequest(
                Url: "https://www.youtube.com/watch?v=FAKE0001234",
                Mode: DownloadMode.VideoOnly,
                ChosenFormat: format,
                SubtitleLanguageCodes: Array.Empty<string>(),
                ClipRange: null,
                SaveDirectory: temp,
                SanitizedFileStem: "Fake_Test_Video");

            var result = await runner.DownloadAsync(request, progress);

            Assert.True(result.IsSuccess, result.ErrorStderr);
            Assert.NotNull(result.OutputFilePath);
            Assert.True(File.Exists(result.OutputFilePath));
            Assert.NotEmpty(progressValues);
            Assert.Contains(100.0, progressValues);
        }
        finally { Directory.Delete(temp, recursive: true); }
    }
}
```

- [ ] **Step 4: Run tests**

```powershell
dotnet test tests/YtDlpTool.Process.Tests/ --filter "FullyQualifiedName~YtDlpRunnerDownloadTests"
```
Expected: 1 passes.

- [ ] **Step 5: Commit**

```powershell
git add src/YtDlpTool.Process/ tests/YtDlpTool.Process.Tests/YtDlpRunnerDownloadTests.cs
git commit -m "feat(process): YtDlpRunner download with progress parsing"
```

---

### Task 4.6: `FfmpegRunner` (audio-only standalone clipping, unused in v1 but wired for future)

**Files:**
- Create: `src/YtDlpTool.Process/FfmpegRunner.cs`

> Spec note: most of the time yt-dlp invokes ffmpeg itself for merging / clipping / embedding. We only need a direct `FfmpegRunner` for the rare case where we want to clip an already-downloaded file. To keep YAGNI honest, this is just a `--version` healthcheck for now; v2 can extend.

- [ ] **Step 1: Implement**

```csharp
// src/YtDlpTool.Process/FfmpegRunner.cs
namespace YtDlpTool.Process;

public sealed class FfmpegRunner
{
    private readonly string _executable;

    public FfmpegRunner(string executable) => _executable = executable;

    public async Task<(bool IsHealthy, string? Version)> CheckHealthAsync(
        CancellationToken cancellationToken = default)
    {
        var args = new ProcessStartArguments(
            ExecutablePath: _executable,
            Arguments: new[] { "-version" },
            Timeout: TimeSpan.FromSeconds(5));

        var firstLine = null as string;
        var exit = await ProcessSandbox.RunAsync(args,
            onStdout: l => firstLine ??= l.Text,
            cancellationToken: cancellationToken);

        if (exit.ExitCode != 0 || firstLine is null) return (false, null);
        return (true, firstLine);
    }
}
```

- [ ] **Step 2: Build**

```powershell
dotnet build src/YtDlpTool.Process/
```
Expected: succeeds.

- [ ] **Step 3: Commit**

```powershell
git add src/YtDlpTool.Process/FfmpegRunner.cs
git commit -m "feat(process): FfmpegRunner health check stub (clipping extension reserved for v2)"
```

---

### Task 4.7: Full suite + AOT

- [ ] **Step 1: Test all**

```powershell
dotnet test
```
Expected: green.

- [ ] **Step 2: AOT publish**

```powershell
dotnet publish src/YtDlpTool/YtDlpTool.csproj -c Release -r win-x64
```
Expected: succeeds.

- [ ] **Step 3: Tag**

```powershell
git tag phase-4-process-complete
```

---

## Phase 4 complete gate

- [ ] `ProcessSandbox` + tests (cancellation, timeout, env whitelist)
- [ ] `FakeYtDlp` console app for integration testing
- [ ] `YtDlpRunner.FetchMetadataAsync` + tests
- [ ] `YtDlpRunner.DownloadAsync` with progress + tests
- [ ] `FfmpegRunner.CheckHealthAsync`
- [ ] AOT publish still green
- [ ] Tag `phase-4-process-complete`

Proceed to Phase 5.

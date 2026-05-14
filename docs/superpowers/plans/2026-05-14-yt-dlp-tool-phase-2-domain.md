# Phase 2 · Domain Layer

**Goal:** Build pure-C# Domain types with full test coverage: models, URL validation, filename sanitization, time-range validation, paths, config storage, logger.

**Prerequisites:** Phase 1 complete (tag `phase-1-aot-verified`).

---

### Task 2.1: `AppPaths` — resolve writable folders

**Files:**
- Create: `src/YtDlpTool.Domain/Persistence/AppPaths.cs`
- Create: `tests/YtDlpTool.Domain.Tests/Persistence/AppPathsTests.cs`

`AppPaths` resolves the location of `config.json`, `logs/`, `state.log`, `.update/` etc. Spec: when app is launched from read-only location (USB), state writes go to `%LOCALAPPDATA%\YtDlpTool\` shadow.

- [ ] **Step 1: Write failing test**

```csharp
// tests/YtDlpTool.Domain.Tests/Persistence/AppPathsTests.cs
using YtDlpTool.Domain.Persistence;

namespace YtDlpTool.Domain.Tests.Persistence;

public class AppPathsTests
{
    [Fact]
    public void DataRoot_WritableAppDir_UsesAppDir()
    {
        var tempApp = Path.Combine(Path.GetTempPath(), "ytdlp-test-" + Guid.NewGuid());
        Directory.CreateDirectory(tempApp);
        try
        {
            var paths = AppPaths.ResolveForAppDirectory(tempApp);
            Assert.Equal(tempApp, paths.AppDirectory);
            Assert.Equal(tempApp, paths.DataRoot);
            Assert.Equal(Path.Combine(tempApp, "config.json"), paths.ConfigFile);
            Assert.Equal(Path.Combine(tempApp, "logs"), paths.LogsDirectory);
            Assert.Equal(Path.Combine(tempApp, "state.log"), paths.StateLog);
            Assert.Equal(Path.Combine(tempApp, ".update"), paths.UpdateStaging);
            Assert.Equal(Path.Combine(tempApp, "bin"), paths.BinDirectory);
        }
        finally { Directory.Delete(tempApp, recursive: true); }
    }

    [Fact]
    public void DataRoot_ReadOnlyAppDir_UsesLocalAppData()
    {
        var paths = AppPaths.ResolveForAppDirectory(
            appDir: @"C:\Some\ReadOnly\Path",
            isWritable: _ => false);
        var expectedShadow = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "YtDlpTool");
        Assert.Equal(expectedShadow, paths.DataRoot);
        Assert.Equal(Path.Combine(expectedShadow, "config.json"), paths.ConfigFile);
        Assert.Equal(@"C:\Some\ReadOnly\Path\bin", paths.BinDirectory);
    }
}
```

- [ ] **Step 2: Run — expect failure**

```powershell
dotnet test tests/YtDlpTool.Domain.Tests/ --filter "FullyQualifiedName~AppPathsTests"
```
Expected: compile error (`AppPaths` not found).

- [ ] **Step 3: Implement**

```csharp
// src/YtDlpTool.Domain/Persistence/AppPaths.cs
namespace YtDlpTool.Domain.Persistence;

public sealed class AppPaths
{
    public string AppDirectory { get; }
    public string DataRoot { get; }
    public string ConfigFile => Path.Combine(DataRoot, "config.json");
    public string LogsDirectory => Path.Combine(DataRoot, "logs");
    public string StateLog => Path.Combine(DataRoot, "state.log");
    public string UpdateStaging => Path.Combine(DataRoot, ".update");
    public string BinDirectory => Path.Combine(AppDirectory, "bin");

    private AppPaths(string appDir, string dataRoot)
    {
        AppDirectory = appDir;
        DataRoot = dataRoot;
    }

    public static AppPaths ResolveForAppDirectory(
        string appDir,
        Func<string, bool>? isWritable = null)
    {
        isWritable ??= TestWritable;
        var dataRoot = isWritable(appDir)
            ? appDir
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "YtDlpTool");
        return new AppPaths(appDir, dataRoot);
    }

    public static AppPaths ResolveForCurrentProcess() =>
        ResolveForAppDirectory(AppContext.BaseDirectory);

    private static bool TestWritable(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return false;
            var probe = Path.Combine(dir, $".write-probe-{Guid.NewGuid()}");
            File.WriteAllText(probe, "x");
            File.Delete(probe);
            return true;
        }
        catch { return false; }
    }

    public void EnsureDataDirectoriesExist()
    {
        Directory.CreateDirectory(DataRoot);
        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(UpdateStaging);
    }
}
```

- [ ] **Step 4: Run test**

```powershell
dotnet test tests/YtDlpTool.Domain.Tests/ --filter "FullyQualifiedName~AppPathsTests"
```
Expected: `Passed!  Failed: 0, Passed: 2`.

- [ ] **Step 5: Commit**

```powershell
git add src/YtDlpTool.Domain/Persistence/AppPaths.cs tests/YtDlpTool.Domain.Tests/Persistence/AppPathsTests.cs
git commit -m "feat(domain): AppPaths resolves writable data root"
```

---

### Task 2.2: `UrlValidator` — YouTube URL strict whitelist

**Files:**
- Create: `src/YtDlpTool.Domain/Services/UrlValidator.cs`
- Create: `src/YtDlpTool.Domain/Services/UrlValidationResult.cs`
- Create: `tests/YtDlpTool.Domain.Tests/Services/UrlValidatorTests.cs`

Spec 5.1: host whitelist, canonical reconstruction, only https, reject IP literal / URL-encoded host / IDN homograph.

- [ ] **Step 1: Write failing tests**

```csharp
// tests/YtDlpTool.Domain.Tests/Services/UrlValidatorTests.cs
using YtDlpTool.Domain.Services;

namespace YtDlpTool.Domain.Tests.Services;

public class UrlValidatorTests
{
    private readonly UrlValidator _v = new();

    [Theory]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ", "https://www.youtube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://youtube.com/watch?v=dQw4w9WgXcQ", "https://www.youtube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://youtu.be/dQw4w9WgXcQ", "https://www.youtube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://youtu.be/dQw4w9WgXcQ?t=42", "https://www.youtube.com/watch?v=dQw4w9WgXcQ&t=42")]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ&list=PLxxxx", "https://www.youtube.com/watch?v=dQw4w9WgXcQ")]
    public void Validate_AcceptsAndCanonicalizes(string input, string expected)
    {
        var result = _v.Validate(input);
        Assert.True(result.IsValid, result.Reason);
        Assert.Equal(expected, result.CanonicalUrl);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a url")]
    [InlineData("http://www.youtube.com/watch?v=abc")]                  // http not https
    [InlineData("https://www.youtube.com/watch")]                       // missing video id
    [InlineData("https://www.youtube.com.evil.com/watch?v=abc")]        // host prefix attack
    [InlineData("https://192.168.1.1/watch?v=abc")]                     // IP literal
    [InlineData("https://www.yоutube.com/watch?v=abc")]            // cyrillic homograph
    [InlineData("https://%77ww.youtube.com/watch?v=abc")]               // url-encoded host
    [InlineData("file:///C:/etc/passwd")]
    [InlineData("javascript:alert(1)")]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ&v=other")] // multiple v=
    public void Validate_RejectsAttackVectors(string input)
    {
        var result = _v.Validate(input);
        Assert.False(result.IsValid);
        Assert.NotNull(result.Reason);
    }
}
```

- [ ] **Step 2: Run — expect failure**

Expected: compile errors (types missing).

- [ ] **Step 3: Implement result record**

```csharp
// src/YtDlpTool.Domain/Services/UrlValidationResult.cs
namespace YtDlpTool.Domain.Services;

public sealed record UrlValidationResult(bool IsValid, string? CanonicalUrl, string? Reason)
{
    public static UrlValidationResult Ok(string canonical) => new(true, canonical, null);
    public static UrlValidationResult Fail(string reason) => new(false, null, reason);
}
```

- [ ] **Step 4: Implement validator**

```csharp
// src/YtDlpTool.Domain/Services/UrlValidator.cs
using System.Text.RegularExpressions;

namespace YtDlpTool.Domain.Services;

public sealed class UrlValidator
{
    private static readonly HashSet<string> AllowedHosts =
        new(StringComparer.OrdinalIgnoreCase) { "www.youtube.com", "youtube.com", "youtu.be" };

    private static readonly Regex VideoIdPattern =
        new(@"^[A-Za-z0-9_-]{11}$", RegexOptions.Compiled);

    public UrlValidationResult Validate(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return UrlValidationResult.Fail("空網址");

        if (input.Contains('%'))
            return UrlValidationResult.Fail("不接受 URL-encoded 主機");

        if (!Uri.TryCreate(input, UriKind.Absolute, out var uri))
            return UrlValidationResult.Fail("不是有效的網址");

        if (uri.Scheme != "https")
            return UrlValidationResult.Fail("只接受 https");

        var host = uri.IdnHost;
        if (host.Length != uri.Host.Length)
            return UrlValidationResult.Fail("拒絕含 IDN/Unicode 的主機（防 homograph）");

        if (!AllowedHosts.Contains(host))
            return UrlValidationResult.Fail($"不允許的主機：{host}");

        if (IsIpLiteral(host))
            return UrlValidationResult.Fail("不接受 IP 位址");

        string? videoId;
        long? startSeconds = null;

        if (string.Equals(host, "youtu.be", StringComparison.OrdinalIgnoreCase))
        {
            videoId = uri.AbsolutePath.TrimStart('/');
            startSeconds = ParseTimeQuery(uri.Query, "t");
        }
        else
        {
            if (!string.Equals(uri.AbsolutePath, "/watch", StringComparison.OrdinalIgnoreCase))
                return UrlValidationResult.Fail("不支援這個路徑");

            var v = GetSingleQueryValue(uri.Query, "v");
            if (v is null) return UrlValidationResult.Fail("找不到影片 ID");
            videoId = v;
            startSeconds = ParseTimeQuery(uri.Query, "t");
        }

        if (videoId is null || !VideoIdPattern.IsMatch(videoId))
            return UrlValidationResult.Fail("影片 ID 格式錯誤");

        var canonical = $"https://www.youtube.com/watch?v={videoId}";
        if (startSeconds is not null) canonical += $"&t={startSeconds}";
        return UrlValidationResult.Ok(canonical);
    }

    private static bool IsIpLiteral(string host) =>
        System.Net.IPAddress.TryParse(host, out _);

    private static string? GetSingleQueryValue(string query, string key)
    {
        if (string.IsNullOrEmpty(query)) return null;
        var trimmed = query.StartsWith('?') ? query[1..] : query;
        string? found = null;
        foreach (var pair in trimmed.Split('&'))
        {
            var eq = pair.IndexOf('=');
            if (eq <= 0) continue;
            var k = pair[..eq];
            if (!string.Equals(k, key, StringComparison.Ordinal)) continue;
            if (found is not null) return null; // duplicate keys → reject
            found = Uri.UnescapeDataString(pair[(eq + 1)..]);
        }
        return found;
    }

    private static long? ParseTimeQuery(string query, string key)
    {
        var raw = GetSingleQueryValue(query, key);
        if (raw is null) return null;
        if (long.TryParse(raw.TrimEnd('s'), out var seconds) && seconds >= 0)
            return seconds;
        return null;
    }
}
```

- [ ] **Step 5: Run tests**

```powershell
dotnet test tests/YtDlpTool.Domain.Tests/ --filter "FullyQualifiedName~UrlValidatorTests"
```
Expected: all 16+ test cases pass.

- [ ] **Step 6: Commit**

```powershell
git add src/YtDlpTool.Domain/Services/UrlValidator.cs src/YtDlpTool.Domain/Services/UrlValidationResult.cs tests/YtDlpTool.Domain.Tests/Services/UrlValidatorTests.cs
git commit -m "feat(domain): UrlValidator with host whitelist and attack-vector tests"
```

---

### Task 2.3: `FileNameSanitizer` — safe filenames

**Files:**
- Create: `src/YtDlpTool.Domain/Services/FileNameSanitizer.cs`
- Create: `tests/YtDlpTool.Domain.Tests/Services/FileNameSanitizerTests.cs`

Spec 5.1: remove `< > : " / \ | ? *`, control chars, trailing dots/spaces, U+202E; truncate 200; empty fallback `video_<timestamp>`.

- [ ] **Step 1: Write failing tests**

```csharp
// tests/YtDlpTool.Domain.Tests/Services/FileNameSanitizerTests.cs
using YtDlpTool.Domain.Services;

namespace YtDlpTool.Domain.Tests.Services;

public class FileNameSanitizerTests
{
    [Theory]
    [InlineData("Hello World", "Hello World")]
    [InlineData("a:b/c\\d|e?f*g\"h<i>j", "a_b_c_d_e_f_g_h_i_j")]
    [InlineData("trailing dots...", "trailing dots")]
    [InlineData("  spaces  ", "spaces")]
    [InlineData("CON", "_CON")]
    [InlineData("aux.txt", "_aux.txt")]
    [InlineData("normal.mp4", "normal.mp4")]
    public void Sanitize_StandardCases(string input, string expected)
    {
        Assert.Equal(expected, FileNameSanitizer.Sanitize(input));
    }

    [Fact]
    public void Sanitize_RemovesControlChars()
    {
        var result = FileNameSanitizer.Sanitize("hello world");
        Assert.Equal("helloworld", result);
    }

    [Fact]
    public void Sanitize_RemovesRightToLeftOverride()
    {
        var result = FileNameSanitizer.Sanitize("photo‮gnp.exe");
        Assert.DoesNotContain('‮', result);
    }

    [Fact]
    public void Sanitize_Truncates_To200()
    {
        var input = new string('a', 500);
        var result = FileNameSanitizer.Sanitize(input);
        Assert.Equal(200, result.Length);
    }

    [Fact]
    public void Sanitize_PreservesExtensionWhenTruncating()
    {
        var input = new string('a', 500) + ".mp4";
        var result = FileNameSanitizer.Sanitize(input);
        Assert.EndsWith(".mp4", result);
        Assert.Equal(200, result.Length);
    }

    [Fact]
    public void Sanitize_EmptyReturnsFallback()
    {
        var result = FileNameSanitizer.Sanitize("   ");
        Assert.StartsWith("video_", result);
    }
}
```

- [ ] **Step 2: Run — expect failure**

Expected: compile error.

- [ ] **Step 3: Implement**

```csharp
// src/YtDlpTool.Domain/Services/FileNameSanitizer.cs
using System.Text;

namespace YtDlpTool.Domain.Services;

public static class FileNameSanitizer
{
    private const int MaxLength = 200;

    private static readonly HashSet<char> Forbidden = new("<>:\"/\\|?*".ToCharArray());

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON","PRN","AUX","NUL",
        "COM1","COM2","COM3","COM4","COM5","COM6","COM7","COM8","COM9",
        "LPT1","LPT2","LPT3","LPT4","LPT5","LPT6","LPT7","LPT8","LPT9"
    };

    public static string Sanitize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return $"video_{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";

        var sb = new StringBuilder(raw.Length);
        foreach (var c in raw)
        {
            if (Forbidden.Contains(c)) { sb.Append('_'); continue; }
            if (char.IsControl(c)) continue;
            if (c == '‮' || c == '‭' || c == '‎' || c == '‏') continue;
            sb.Append(c);
        }

        var cleaned = sb.ToString().TrimEnd(' ', '.');

        cleaned = cleaned.TrimStart();
        if (cleaned.Length == 0) return $"video_{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";

        var dot = cleaned.LastIndexOf('.');
        var stem = dot > 0 ? cleaned[..dot] : cleaned;
        var ext  = dot > 0 ? cleaned[dot..] : "";

        if (ReservedNames.Contains(stem))
            cleaned = "_" + cleaned;

        if (cleaned.Length > MaxLength)
        {
            var room = MaxLength - ext.Length;
            cleaned = stem[..Math.Max(1, room)] + ext;
        }

        return cleaned;
    }
}
```

- [ ] **Step 4: Run tests**

```powershell
dotnet test tests/YtDlpTool.Domain.Tests/ --filter "FullyQualifiedName~FileNameSanitizerTests"
```
Expected: all pass.

- [ ] **Step 5: Commit**

```powershell
git add src/YtDlpTool.Domain/Services/FileNameSanitizer.cs tests/YtDlpTool.Domain.Tests/Services/FileNameSanitizerTests.cs
git commit -m "feat(domain): FileNameSanitizer with windows-reserved & U+202E handling"
```

---

### Task 2.4: `TimeRange` model + `TimeRangeValidator`

**Files:**
- Create: `src/YtDlpTool.Domain/Models/TimeRange.cs`
- Create: `src/YtDlpTool.Domain/Services/TimeRangeValidator.cs`
- Create: `tests/YtDlpTool.Domain.Tests/Services/TimeRangeValidatorTests.cs`

Spec 5.1: regex `^\d{1,2}:\d{2}:\d{2}$`, start < end ≤ duration, max 8 hours.

- [ ] **Step 1: Write failing tests**

```csharp
// tests/YtDlpTool.Domain.Tests/Services/TimeRangeValidatorTests.cs
using YtDlpTool.Domain.Models;
using YtDlpTool.Domain.Services;

namespace YtDlpTool.Domain.Tests.Services;

public class TimeRangeValidatorTests
{
    [Theory]
    [InlineData("00:00:00", "00:01:30")]
    [InlineData("01:23:45", "02:34:56")]
    public void Validate_Accepts_GoodRanges(string s, string e)
    {
        var r = TimeRangeValidator.Parse(s, e, videoDuration: TimeSpan.FromHours(3));
        Assert.True(r.IsValid, r.Reason);
        Assert.NotNull(r.Range);
    }

    [Theory]
    [InlineData("1:2:3", "00:01:30")]       // bad format
    [InlineData("00:60:00", "00:01:30")]    // 60 min invalid? actually 60 should be rejected (use hh:mm:ss canonical)
    [InlineData("00:01:00", "00:01:00")]    // start == end
    [InlineData("00:02:00", "00:01:30")]    // start > end
    public void Validate_Rejects_BadFormatOrOrder(string s, string e)
    {
        var r = TimeRangeValidator.Parse(s, e, videoDuration: TimeSpan.FromHours(3));
        Assert.False(r.IsValid);
    }

    [Fact]
    public void Validate_Rejects_EndPastDuration()
    {
        var r = TimeRangeValidator.Parse("00:00:00", "01:00:00", videoDuration: TimeSpan.FromMinutes(30));
        Assert.False(r.IsValid);
        Assert.Contains("超過", r.Reason);
    }

    [Fact]
    public void Validate_Rejects_LongerThan8Hours()
    {
        var r = TimeRangeValidator.Parse("00:00:00", "09:00:00", videoDuration: TimeSpan.FromHours(10));
        Assert.False(r.IsValid);
        Assert.Contains("8", r.Reason);
    }
}
```

- [ ] **Step 2: Run — expect failure**

- [ ] **Step 3: Implement `TimeRange`**

```csharp
// src/YtDlpTool.Domain/Models/TimeRange.cs
namespace YtDlpTool.Domain.Models;

public sealed record TimeRange(TimeSpan Start, TimeSpan End)
{
    public TimeSpan Duration => End - Start;

    public string ToYtDlpFormat() =>
        $"*{Start:hh\\:mm\\:ss}-{End:hh\\:mm\\:ss}";
}
```

- [ ] **Step 4: Implement validator**

```csharp
// src/YtDlpTool.Domain/Services/TimeRangeValidator.cs
using System.Text.RegularExpressions;
using YtDlpTool.Domain.Models;

namespace YtDlpTool.Domain.Services;

public static class TimeRangeValidator
{
    private static readonly Regex Pattern =
        new(@"^([0-1]?\d|2[0-3]):[0-5]\d:[0-5]\d$", RegexOptions.Compiled);

    private static readonly TimeSpan MaxClipLength = TimeSpan.FromHours(8);

    public sealed record Result(bool IsValid, TimeRange? Range, string? Reason)
    {
        public static Result Ok(TimeRange r) => new(true, r, null);
        public static Result Fail(string reason) => new(false, null, reason);
    }

    public static Result Parse(string startText, string endText, TimeSpan videoDuration)
    {
        if (!Pattern.IsMatch(startText))
            return Result.Fail("開始時間格式錯誤（請用 hh:mm:ss）");
        if (!Pattern.IsMatch(endText))
            return Result.Fail("結束時間格式錯誤（請用 hh:mm:ss）");

        var start = TimeSpan.Parse(startText);
        var end   = TimeSpan.Parse(endText);

        if (end <= start)
            return Result.Fail("結束時間必須晚於開始時間");

        if (end > videoDuration)
            return Result.Fail($"結束時間超過影片長度（{videoDuration:hh\\:mm\\:ss}）");

        if ((end - start) > MaxClipLength)
            return Result.Fail("擷取片段長度不可超過 8 小時");

        return Result.Ok(new TimeRange(start, end));
    }
}
```

- [ ] **Step 5: Run tests**

```powershell
dotnet test tests/YtDlpTool.Domain.Tests/ --filter "FullyQualifiedName~TimeRangeValidatorTests"
```
Expected: all pass.

- [ ] **Step 6: Commit**

```powershell
git add src/YtDlpTool.Domain/Models/TimeRange.cs src/YtDlpTool.Domain/Services/TimeRangeValidator.cs tests/YtDlpTool.Domain.Tests/Services/TimeRangeValidatorTests.cs
git commit -m "feat(domain): TimeRange model + validator (8h max, bounds checked)"
```

---

### Task 2.5: Core model types

**Files:**
- Create: `src/YtDlpTool.Domain/Models/DownloadMode.cs`
- Create: `src/YtDlpTool.Domain/Models/JobStatus.cs`
- Create: `src/YtDlpTool.Domain/Models/VideoFormat.cs`
- Create: `src/YtDlpTool.Domain/Models/SubtitleTrack.cs`
- Create: `src/YtDlpTool.Domain/Models/VideoMetadata.cs`
- Create: `src/YtDlpTool.Domain/Models/DownloadJob.cs`

These are value types — minimal logic, mostly data carriers. No tests required for pure records (they're tested via consumers in later tasks).

- [ ] **Step 1: Create `DownloadMode`**

```csharp
// src/YtDlpTool.Domain/Models/DownloadMode.cs
namespace YtDlpTool.Domain.Models;

public enum DownloadMode
{
    AudioOnly,
    VideoOnly,
    AudioAndVideo
}
```

- [ ] **Step 2: Create `JobStatus`**

```csharp
// src/YtDlpTool.Domain/Models/JobStatus.cs
namespace YtDlpTool.Domain.Models;

public enum JobStatus
{
    Pending,
    Downloading,
    Completed,
    Failed,
    Cancelled
}
```

- [ ] **Step 3: Create `VideoFormat`**

```csharp
// src/YtDlpTool.Domain/Models/VideoFormat.cs
namespace YtDlpTool.Domain.Models;

public sealed record VideoFormat(
    string FormatId,
    int? Height,
    string? VideoCodec,
    string? AudioCodec,
    string Extension,
    long? FileSizeBytes,
    int? AudioBitrateKbps);
```

- [ ] **Step 4: Create `SubtitleTrack`**

```csharp
// src/YtDlpTool.Domain/Models/SubtitleTrack.cs
namespace YtDlpTool.Domain.Models;

public sealed record SubtitleTrack(
    string LanguageCode,
    string DisplayName,
    bool IsAutoGenerated);
```

- [ ] **Step 5: Create `VideoMetadata`**

```csharp
// src/YtDlpTool.Domain/Models/VideoMetadata.cs
namespace YtDlpTool.Domain.Models;

public sealed record VideoMetadata(
    string VideoId,
    string Title,
    string Channel,
    TimeSpan Duration,
    string ThumbnailUrl,
    IReadOnlyList<VideoFormat> Formats,
    IReadOnlyList<SubtitleTrack> Subtitles);
```

- [ ] **Step 6: Create `DownloadJob`**

```csharp
// src/YtDlpTool.Domain/Models/DownloadJob.cs
namespace YtDlpTool.Domain.Models;

public sealed class DownloadJob
{
    public Guid Id { get; }
    public string Url { get; }
    public string Title { get; }
    public string ThumbnailUrl { get; }
    public DownloadMode Mode { get; }
    public VideoFormat ChosenFormat { get; }
    public IReadOnlyList<string> SubtitleLanguageCodes { get; }
    public TimeRange? ClipRange { get; }
    public string SaveDirectory { get; }

    public JobStatus Status { get; private set; } = JobStatus.Pending;
    public double Progress { get; private set; }
    public long? BytesPerSecond { get; private set; }
    public TimeSpan? Eta { get; private set; }
    public string? FailureReason { get; private set; }
    public string? FailureCode { get; private set; }
    public string? OutputFilePath { get; private set; }

    public DownloadJob(
        string url, string title, string thumbnailUrl,
        DownloadMode mode, VideoFormat chosenFormat,
        IReadOnlyList<string> subtitleLanguageCodes,
        TimeRange? clipRange, string saveDirectory)
    {
        Id = Guid.NewGuid();
        Url = url;
        Title = title;
        ThumbnailUrl = thumbnailUrl;
        Mode = mode;
        ChosenFormat = chosenFormat;
        SubtitleLanguageCodes = subtitleLanguageCodes;
        ClipRange = clipRange;
        SaveDirectory = saveDirectory;
    }

    public void MarkDownloading() => Status = JobStatus.Downloading;
    public void MarkCancelled()   => Status = JobStatus.Cancelled;

    public void ReportProgress(double percent, long? bps, TimeSpan? eta)
    {
        Progress = percent;
        BytesPerSecond = bps;
        Eta = eta;
    }

    public void MarkCompleted(string outputPath)
    {
        Status = JobStatus.Completed;
        Progress = 100.0;
        OutputFilePath = outputPath;
    }

    public void MarkFailed(string reason, string code)
    {
        Status = JobStatus.Failed;
        FailureReason = reason;
        FailureCode = code;
    }
}
```

- [ ] **Step 7: Build**

```powershell
dotnet build src/YtDlpTool.Domain/
```
Expected: succeeds.

- [ ] **Step 8: Commit**

```powershell
git add src/YtDlpTool.Domain/Models/
git commit -m "feat(domain): core models (DownloadMode, JobStatus, VideoFormat, SubtitleTrack, VideoMetadata, DownloadJob)"
```

---

### Task 2.6: `AppConfig` model + JSON source generator context

**Files:**
- Create: `src/YtDlpTool.Domain/Models/AppConfig.cs`
- Create: `src/YtDlpTool.Domain/Models/UpdateCheckFrequency.cs`
- Create: `src/YtDlpTool.Domain/Models/ThemePreference.cs`
- Create: `src/YtDlpTool.Domain/Persistence/AppJsonContext.cs`

System.Text.Json source generator is required for NativeAOT — reflection-based JSON breaks AOT.

- [ ] **Step 1: Create enums**

```csharp
// src/YtDlpTool.Domain/Models/UpdateCheckFrequency.cs
namespace YtDlpTool.Domain.Models;

public enum UpdateCheckFrequency
{
    Never,
    EveryLaunch,
    Daily,
    Weekly,
    Monthly
}
```

```csharp
// src/YtDlpTool.Domain/Models/ThemePreference.cs
namespace YtDlpTool.Domain.Models;

public enum ThemePreference { System, Light, Dark }
```

- [ ] **Step 2: Create `AppConfig`**

```csharp
// src/YtDlpTool.Domain/Models/AppConfig.cs
namespace YtDlpTool.Domain.Models;

public sealed class AppConfig
{
    public int ConcurrentDownloads { get; set; } = 2;
    public string DefaultSaveDirectory { get; set; } = "";
    public UpdateCheckFrequency YtDlpCheckFrequency { get; set; } = UpdateCheckFrequency.Weekly;
    public UpdateCheckFrequency FfmpegCheckFrequency { get; set; } = UpdateCheckFrequency.Monthly;
    public UpdateCheckFrequency AppCheckFrequency { get; set; } = UpdateCheckFrequency.Monthly;
    public ThemePreference Theme { get; set; } = ThemePreference.System;
    public string LanguageCode { get; set; } = "zh-TW";
    public DateTimeOffset? LastYtDlpCheck { get; set; }
    public DateTimeOffset? LastFfmpegCheck { get; set; }
    public DateTimeOffset? LastAppCheck { get; set; }
    public string LogLevel { get; set; } = "Info";

    public static AppConfig CreateDefault()
    {
        return new AppConfig
        {
            DefaultSaveDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Downloads", "YtDlpTool")
        };
    }
}
```

- [ ] **Step 3: Create JSON source generator context**

```csharp
// src/YtDlpTool.Domain/Persistence/AppJsonContext.cs
using System.Text.Json.Serialization;
using YtDlpTool.Domain.Models;

namespace YtDlpTool.Domain.Persistence;

[JsonSourceGenerationOptions(
    WriteIndented = true,
    Converters = new[] { typeof(JsonStringEnumConverter) },
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AppConfig))]
public partial class AppJsonContext : JsonSerializerContext
{
}
```

- [ ] **Step 4: Build**

```powershell
dotnet build src/YtDlpTool.Domain/
```
Expected: succeeds. The source generator runs at compile time and produces a partial implementation.

- [ ] **Step 5: Commit**

```powershell
git add src/YtDlpTool.Domain/Models/AppConfig.cs src/YtDlpTool.Domain/Models/UpdateCheckFrequency.cs src/YtDlpTool.Domain/Models/ThemePreference.cs src/YtDlpTool.Domain/Persistence/AppJsonContext.cs
git commit -m "feat(domain): AppConfig model + System.Text.Json source-gen context (AOT-safe)"
```

---

### Task 2.7: `ConfigStore` — read/write config.json atomically

**Files:**
- Create: `src/YtDlpTool.Domain/Services/ConfigStore.cs`
- Create: `tests/YtDlpTool.Domain.Tests/Services/ConfigStoreTests.cs`

- [ ] **Step 1: Write failing tests**

```csharp
// tests/YtDlpTool.Domain.Tests/Services/ConfigStoreTests.cs
using YtDlpTool.Domain.Models;
using YtDlpTool.Domain.Services;

namespace YtDlpTool.Domain.Tests.Services;

public class ConfigStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _path;

    public ConfigStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ytdlp-cfg-" + Guid.NewGuid());
        Directory.CreateDirectory(_tempDir);
        _path = Path.Combine(_tempDir, "config.json");
    }

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    [Fact]
    public void Load_MissingFile_ReturnsDefault()
    {
        var store = new ConfigStore(_path);
        var cfg = store.Load();
        Assert.Equal(2, cfg.ConcurrentDownloads);
        Assert.Equal("zh-TW", cfg.LanguageCode);
    }

    [Fact]
    public void SaveThenLoad_Roundtrips()
    {
        var store = new ConfigStore(_path);
        var cfg = AppConfig.CreateDefault();
        cfg.ConcurrentDownloads = 4;
        cfg.Theme = ThemePreference.Dark;
        store.Save(cfg);

        var loaded = store.Load();
        Assert.Equal(4, loaded.ConcurrentDownloads);
        Assert.Equal(ThemePreference.Dark, loaded.Theme);
    }

    [Fact]
    public void Save_AtomicViaTempFile()
    {
        var store = new ConfigStore(_path);
        var cfg = AppConfig.CreateDefault();
        store.Save(cfg);
        Assert.True(File.Exists(_path));
        Assert.False(File.Exists(_path + ".tmp"));
    }

    [Fact]
    public void Load_CorruptFile_ReturnsDefault()
    {
        File.WriteAllText(_path, "{ not valid json");
        var store = new ConfigStore(_path);
        var cfg = store.Load();
        Assert.Equal(2, cfg.ConcurrentDownloads);
    }
}
```

- [ ] **Step 2: Run — expect failure**

- [ ] **Step 3: Implement**

```csharp
// src/YtDlpTool.Domain/Services/ConfigStore.cs
using System.Text.Json;
using YtDlpTool.Domain.Models;
using YtDlpTool.Domain.Persistence;

namespace YtDlpTool.Domain.Services;

public sealed class ConfigStore
{
    private readonly string _path;

    public ConfigStore(string path) => _path = path;

    public AppConfig Load()
    {
        if (!File.Exists(_path)) return AppConfig.CreateDefault();
        try
        {
            using var stream = File.OpenRead(_path);
            var cfg = JsonSerializer.Deserialize(stream, AppJsonContext.Default.AppConfig);
            return cfg ?? AppConfig.CreateDefault();
        }
        catch (JsonException)
        {
            return AppConfig.CreateDefault();
        }
    }

    public void Save(AppConfig config)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var tmp = _path + ".tmp";
        using (var stream = File.Create(tmp))
        {
            JsonSerializer.Serialize(stream, config, AppJsonContext.Default.AppConfig);
        }
        if (File.Exists(_path)) File.Replace(tmp, _path, destinationBackupFileName: null);
        else File.Move(tmp, _path);
    }
}
```

- [ ] **Step 4: Run tests**

```powershell
dotnet test tests/YtDlpTool.Domain.Tests/ --filter "FullyQualifiedName~ConfigStoreTests"
```
Expected: 4 pass.

- [ ] **Step 5: Commit**

```powershell
git add src/YtDlpTool.Domain/Services/ConfigStore.cs tests/YtDlpTool.Domain.Tests/Services/ConfigStoreTests.cs
git commit -m "feat(domain): ConfigStore with atomic write & corrupt-file fallback"
```

---

### Task 2.8: `AppLogger` — privacy-aware structured logging

**Files:**
- Create: `src/YtDlpTool.Domain/Logging/LogLevel.cs`
- Create: `src/YtDlpTool.Domain/Logging/LogEntry.cs`
- Create: `src/YtDlpTool.Domain/Logging/AppLogger.cs`
- Create: `tests/YtDlpTool.Domain.Tests/Logging/AppLoggerTests.cs`

Spec 7.4: levels Error/Warn/Info/Debug, rolling 7 days, **never write URL/title/path full text** — hash suffix only.

- [ ] **Step 1: Write failing tests**

```csharp
// tests/YtDlpTool.Domain.Tests/Logging/AppLoggerTests.cs
using YtDlpTool.Domain.Logging;

namespace YtDlpTool.Domain.Tests.Logging;

public class AppLoggerTests : IDisposable
{
    private readonly string _dir;

    public AppLoggerTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ytdlp-log-" + Guid.NewGuid());
        Directory.CreateDirectory(_dir);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Log_WritesEntryToTodaysFile()
    {
        using var log = new AppLogger(_dir, LogLevel.Info, () => DateTime.Parse("2026-05-14T12:00:00"));
        log.Info("download_started", new Dictionary<string, string> { ["mode"] = "AudioOnly" });
        log.Flush();
        var file = Path.Combine(_dir, "2026-05-14.log");
        Assert.True(File.Exists(file));
        var content = File.ReadAllText(file);
        Assert.Contains("INFO", content);
        Assert.Contains("download_started", content);
        Assert.Contains("AudioOnly", content);
    }

    [Fact]
    public void Log_RespectsLevel()
    {
        using var log = new AppLogger(_dir, LogLevel.Warn, () => DateTime.UtcNow);
        log.Debug("debug_event", null);
        log.Info("info_event", null);
        log.Warn("warn_event", null);
        log.Flush();
        var content = File.ReadAllText(Directory.GetFiles(_dir, "*.log").Single());
        Assert.DoesNotContain("debug_event", content);
        Assert.DoesNotContain("info_event", content);
        Assert.Contains("warn_event", content);
    }

    [Fact]
    public void HashSuffix_SameInputSameOutput()
    {
        var a = AppLogger.HashSuffix("https://youtu.be/dQw4w9WgXcQ");
        var b = AppLogger.HashSuffix("https://youtu.be/dQw4w9WgXcQ");
        Assert.Equal(a, b);
        Assert.Equal(8, a.Length);
    }

    [Fact]
    public void PurgeOlderThan_RemovesOldFiles()
    {
        File.WriteAllText(Path.Combine(_dir, "2020-01-01.log"), "old");
        File.WriteAllText(Path.Combine(_dir, "2099-01-01.log"), "future");
        AppLogger.PurgeOlderThan(_dir, TimeSpan.FromDays(7), DateTime.Parse("2026-05-14T00:00:00"));
        Assert.False(File.Exists(Path.Combine(_dir, "2020-01-01.log")));
        Assert.True(File.Exists(Path.Combine(_dir, "2099-01-01.log")));
    }
}
```

- [ ] **Step 2: Run — expect failure**

- [ ] **Step 3: Implement types**

```csharp
// src/YtDlpTool.Domain/Logging/LogLevel.cs
namespace YtDlpTool.Domain.Logging;

public enum LogLevel { Debug = 0, Info = 1, Warn = 2, Error = 3 }
```

```csharp
// src/YtDlpTool.Domain/Logging/LogEntry.cs
namespace YtDlpTool.Domain.Logging;

public sealed record LogEntry(
    DateTime TimestampUtc,
    LogLevel Level,
    string Category,
    IReadOnlyDictionary<string, string>? Fields);
```

```csharp
// src/YtDlpTool.Domain/Logging/AppLogger.cs
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace YtDlpTool.Domain.Logging;

public sealed class AppLogger : IDisposable
{
    private readonly string _logsDir;
    private readonly LogLevel _minLevel;
    private readonly Func<DateTime> _clock;
    private readonly object _gate = new();
    private StreamWriter? _writer;
    private string? _currentFile;

    public AppLogger(string logsDir, LogLevel minLevel, Func<DateTime> clock)
    {
        _logsDir = logsDir;
        _minLevel = minLevel;
        _clock = clock;
        Directory.CreateDirectory(logsDir);
    }

    public void Debug(string category, IReadOnlyDictionary<string, string>? fields = null) =>
        Write(LogLevel.Debug, category, fields);
    public void Info(string category, IReadOnlyDictionary<string, string>? fields = null) =>
        Write(LogLevel.Info, category, fields);
    public void Warn(string category, IReadOnlyDictionary<string, string>? fields = null) =>
        Write(LogLevel.Warn, category, fields);
    public void Error(string category, IReadOnlyDictionary<string, string>? fields = null) =>
        Write(LogLevel.Error, category, fields);

    private void Write(LogLevel level, string category, IReadOnlyDictionary<string, string>? fields)
    {
        if (level < _minLevel) return;
        var now = _clock();
        var fileForDay = Path.Combine(_logsDir, now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".log");

        lock (_gate)
        {
            if (_currentFile != fileForDay)
            {
                _writer?.Dispose();
                _writer = new StreamWriter(File.Open(fileForDay, FileMode.Append, FileAccess.Write, FileShare.Read))
                {
                    AutoFlush = false
                };
                _currentFile = fileForDay;
            }
            _writer!.Write(now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture));
            _writer.Write(' '); _writer.Write(LevelLabel(level));
            _writer.Write(' '); _writer.Write(category);
            if (fields is not null)
            {
                foreach (var kv in fields)
                {
                    _writer.Write(' '); _writer.Write(kv.Key);
                    _writer.Write('='); _writer.Write(EscapeValue(kv.Value));
                }
            }
            _writer.WriteLine();
        }
    }

    public void Flush()
    {
        lock (_gate) _writer?.Flush();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _writer?.Flush();
            _writer?.Dispose();
            _writer = null;
        }
    }

    private static string LevelLabel(LogLevel l) => l switch
    {
        LogLevel.Debug => "DEBUG",
        LogLevel.Info  => "INFO ",
        LogLevel.Warn  => "WARN ",
        LogLevel.Error => "ERROR",
        _ => "?    "
    };

    private static string EscapeValue(string v) =>
        v.IndexOfAny(new[] { ' ', '\t', '"', '\n', '\r' }) < 0
            ? v
            : "\"" + v.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    public static string HashSuffix(string secret)
    {
        var bytes = Encoding.UTF8.GetBytes(secret);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash, 0, 4).ToLowerInvariant();
    }

    public static void PurgeOlderThan(string logsDir, TimeSpan maxAge, DateTime now)
    {
        if (!Directory.Exists(logsDir)) return;
        var cutoff = now - maxAge;
        var nameRegex = new Regex(@"^(\d{4}-\d{2}-\d{2})\.log$");
        foreach (var file in Directory.EnumerateFiles(logsDir, "*.log"))
        {
            var name = Path.GetFileName(file);
            var m = nameRegex.Match(name);
            if (!m.Success) continue;
            if (DateTime.TryParseExact(m.Groups[1].Value, "yyyy-MM-dd",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) &&
                date < cutoff)
            {
                try { File.Delete(file); } catch { /* best-effort */ }
            }
        }
    }
}
```

- [ ] **Step 4: Run tests**

```powershell
dotnet test tests/YtDlpTool.Domain.Tests/ --filter "FullyQualifiedName~AppLoggerTests"
```
Expected: 4 pass.

- [ ] **Step 5: Commit**

```powershell
git add src/YtDlpTool.Domain/Logging/ tests/YtDlpTool.Domain.Tests/Logging/
git commit -m "feat(domain): AppLogger with date-rolled files, level filter, hash suffix, 7-day purge"
```

---

### Task 2.9: `ErrorMapper` — yt-dlp/ffmpeg stderr → friendly zh-TW messages

**Files:**
- Create: `src/YtDlpTool.Domain/Services/ErrorCategory.cs`
- Create: `src/YtDlpTool.Domain/Services/MappedError.cs`
- Create: `src/YtDlpTool.Domain/Services/ErrorMapper.cs`
- Create: `tests/YtDlpTool.Domain.Tests/Services/ErrorMapperTests.cs`

Spec 7.1 maps each known stderr pattern. The mapper must NEVER return raw stderr.

- [ ] **Step 1: Write failing tests**

```csharp
// tests/YtDlpTool.Domain.Tests/Services/ErrorMapperTests.cs
using YtDlpTool.Domain.Services;

namespace YtDlpTool.Domain.Tests.Services;

public class ErrorMapperTests
{
    [Theory]
    [InlineData("ERROR: unable to download video data: HTTP Error 403: Forbidden", ErrorCategory.YouTubeRefused)]
    [InlineData("ERROR: Sign in to confirm your age", ErrorCategory.YouTubeRefused)]
    [InlineData("ERROR: HTTP Error 429: Too Many Requests", ErrorCategory.RateLimited)]
    [InlineData("ERROR: [youtube] xxxxx: Video unavailable", ErrorCategory.VideoUnavailable)]
    [InlineData("ERROR: [youtube] xxxxx: This video is private.", ErrorCategory.VideoUnavailable)]
    [InlineData("ERROR: [youtube] xxxxx: Premieres in 2 hours", ErrorCategory.PremiereUpcoming)]
    [InlineData("ERROR: unable to download webpage: <urlopen error timed out>", ErrorCategory.NetworkError)]
    [InlineData("ERROR: ffmpeg exited with code 1", ErrorCategory.UnknownError)]
    public void Map_KnownPatterns(string stderr, ErrorCategory expected)
    {
        var r = ErrorMapper.Map(stderr);
        Assert.Equal(expected, r.Category);
        Assert.False(string.IsNullOrWhiteSpace(r.UserMessage));
        Assert.False(r.UserMessage.Contains("Error", StringComparison.Ordinal),
            "user message must not contain English 'Error'");
    }

    [Fact]
    public void Map_EmptyReturnsUnknown()
    {
        var r = ErrorMapper.Map("");
        Assert.Equal(ErrorCategory.UnknownError, r.Category);
    }

    [Fact]
    public void Map_AssignsStableErrorCode()
    {
        var a = ErrorMapper.Map("ERROR: HTTP Error 403: Forbidden");
        var b = ErrorMapper.Map("ERROR: HTTP Error 403: Forbidden");
        Assert.Equal(a.ErrorCode, b.ErrorCode);
        Assert.StartsWith("E-", a.ErrorCode);
    }
}
```

- [ ] **Step 2: Run — expect failure**

- [ ] **Step 3: Implement**

```csharp
// src/YtDlpTool.Domain/Services/ErrorCategory.cs
namespace YtDlpTool.Domain.Services;

public enum ErrorCategory
{
    YouTubeRefused,
    RateLimited,
    NetworkError,
    VideoUnavailable,
    PremiereUpcoming,
    DiskFull,
    FileConflict,
    ComponentMissing,
    UnknownError
}
```

```csharp
// src/YtDlpTool.Domain/Services/MappedError.cs
namespace YtDlpTool.Domain.Services;

public sealed record MappedError(
    ErrorCategory Category,
    string UserMessage,
    string ErrorCode,
    bool CanRetry);
```

```csharp
// src/YtDlpTool.Domain/Services/ErrorMapper.cs
using System.Text.RegularExpressions;

namespace YtDlpTool.Domain.Services;

public static class ErrorMapper
{
    private record Rule(ErrorCategory Cat, string Code, string Message, bool CanRetry, Regex Pattern);

    private static readonly Rule[] Rules =
    {
        new(ErrorCategory.RateLimited,        "E-RATE001",
            "YouTube 暫時限制了下載速度，稍後再試", true,
            new(@"HTTP Error 429|Too Many Requests", RegexOptions.Compiled | RegexOptions.IgnoreCase)),

        new(ErrorCategory.YouTubeRefused,     "E-AUTH001",
            "YouTube 拒絕了這次請求，影片可能有年齡或地區限制（本工具不支援登入下載）", false,
            new(@"HTTP Error 403|Sign in to confirm", RegexOptions.Compiled | RegexOptions.IgnoreCase)),

        new(ErrorCategory.PremiereUpcoming,   "E-PREMIER",
            "這是預定首播的影片，請首播開始後再下載", false,
            new(@"Premieres in|This live event will begin|premiere", RegexOptions.Compiled | RegexOptions.IgnoreCase)),

        new(ErrorCategory.VideoUnavailable,   "E-VIDUNAV",
            "這部影片無法下載（可能已被刪除、設為私人或下架）", false,
            new(@"Video unavailable|This video is private|members[- ]only|removed|has been deleted",
                RegexOptions.Compiled | RegexOptions.IgnoreCase)),

        new(ErrorCategory.NetworkError,       "E-NET001",
            "網路連線中斷，請檢查網路後重試", true,
            new(@"urlopen error|timed out|Connection (refused|reset)|Could not resolve host|getaddrinfo",
                RegexOptions.Compiled | RegexOptions.IgnoreCase)),

        new(ErrorCategory.DiskFull,           "E-DISK001",
            "磁碟空間不足，請清理後再試", false,
            new(@"No space left on device|disk full|enough space",
                RegexOptions.Compiled | RegexOptions.IgnoreCase)),

        new(ErrorCategory.ComponentMissing,   "E-COMP001",
            "處理元件缺失或損毀，請從設定→關於→修復元件重新下載", false,
            new(@"ffmpeg.*not found|ffprobe.*not found|cannot find.*ffmpeg",
                RegexOptions.Compiled | RegexOptions.IgnoreCase)),
    };

    public static MappedError Map(string stderr)
    {
        if (string.IsNullOrWhiteSpace(stderr))
            return new MappedError(ErrorCategory.UnknownError, "下載失敗（無錯誤訊息）", "E-UNKNOWN", false);

        foreach (var r in Rules)
            if (r.Pattern.IsMatch(stderr))
                return new MappedError(r.Cat, r.Message, r.Code, r.CanRetry);

        return new MappedError(ErrorCategory.UnknownError,
            $"下載失敗（錯誤代碼 E-{HashCode(stderr)}）", $"E-{HashCode(stderr)}", false);
    }

    private static string HashCode(string s)
    {
        // Stable 6-hex from SHA-256 prefix; not used for security, only correlation.
        var bytes = System.Text.Encoding.UTF8.GetBytes(s);
        var hash = System.Security.Cryptography.SHA256.HashData(bytes);
        return Convert.ToHexString(hash, 0, 3).ToUpperInvariant();
    }
}
```

- [ ] **Step 4: Run tests**

```powershell
dotnet test tests/YtDlpTool.Domain.Tests/ --filter "FullyQualifiedName~ErrorMapperTests"
```
Expected: all pass.

- [ ] **Step 5: Commit**

```powershell
git add src/YtDlpTool.Domain/Services/ErrorCategory.cs src/YtDlpTool.Domain/Services/MappedError.cs src/YtDlpTool.Domain/Services/ErrorMapper.cs tests/YtDlpTool.Domain.Tests/Services/ErrorMapperTests.cs
git commit -m "feat(domain): ErrorMapper with classified zh-TW messages, no raw stderr leak"
```

---

### Task 2.10: Verify full test suite + NativeAOT

- [ ] **Step 1: Run all tests**

```powershell
dotnet test
```
Expected: ~20+ tests pass across Domain tests project.

- [ ] **Step 2: NativeAOT publish**

```powershell
dotnet publish src/YtDlpTool/YtDlpTool.csproj -c Release -r win-x64
```
Expected: succeeds. No `IL2026` / `IL3050` warnings about reflection.

- [ ] **Step 3: Run published exe (sanity)**

Same as Phase 1.9 Step 3 — window opens.

- [ ] **Step 4: Tag**

```powershell
git tag phase-2-domain-complete
```

---

## Phase 2 complete gate

- [ ] `AppPaths`, `UrlValidator`, `FileNameSanitizer`, `TimeRangeValidator`, `ConfigStore`, `AppLogger`, `ErrorMapper` all exist and tested
- [ ] All Domain models (`DownloadMode`, `JobStatus`, `VideoFormat`, `SubtitleTrack`, `VideoMetadata`, `DownloadJob`, `AppConfig`, `TimeRange`) defined
- [ ] `AppJsonContext` source generator working
- [ ] `dotnet test` green
- [ ] NativeAOT publish green
- [ ] Tag `phase-2-domain-complete`

Proceed to Phase 3.

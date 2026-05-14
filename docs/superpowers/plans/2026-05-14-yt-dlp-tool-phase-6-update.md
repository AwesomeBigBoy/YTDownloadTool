# Phase 6 · Update System

**Goal:** Implement `UpdateManifest`, `UpdateChecker` (HTTPS to GitHub, signature-verified), and `UpdateApplier` (download → SHA-256 → Sigstore verify → atomic rename → rollback).

**Prerequisites:** Phase 5 complete (tag `phase-5-queue-complete`).

---

### Task 6.1: Manifest model + JSON context

**Files:**
- Create: `src/YtDlpTool.Domain/Updates/UpdateManifest.cs`
- Create: `src/YtDlpTool.Domain/Updates/ManifestFileEntry.cs`
- Create: `src/YtDlpTool.Domain/Updates/UpdateComponent.cs`
- Modify: `src/YtDlpTool.Domain/Persistence/AppJsonContext.cs`

- [ ] **Step 1: Create types**

```csharp
// src/YtDlpTool.Domain/Updates/UpdateComponent.cs
namespace YtDlpTool.Domain.Updates;

public enum UpdateComponent { App, YtDlp, Ffmpeg }
```

```csharp
// src/YtDlpTool.Domain/Updates/ManifestFileEntry.cs
namespace YtDlpTool.Domain.Updates;

public sealed class ManifestFileEntry
{
    public string Name { get; set; } = "";
    public UpdateComponent Component { get; set; }
    public string Version { get; set; } = "";
    public string DownloadUrl { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public string SignatureUrl { get; set; } = "";
    public string TargetRelativePath { get; set; } = "";
}
```

```csharp
// src/YtDlpTool.Domain/Updates/UpdateManifest.cs
namespace YtDlpTool.Domain.Updates;

public sealed class UpdateManifest
{
    public string ManifestVersion { get; set; } = "1";
    public DateTimeOffset PublishedAt { get; set; }
    public string AppVersion { get; set; } = "";
    public string YtDlpVersion { get; set; } = "";
    public string FfmpegVersion { get; set; } = "";
    public List<ManifestFileEntry> Files { get; set; } = new();
}
```

- [ ] **Step 2: Extend `AppJsonContext`**

Modify `src/YtDlpTool.Domain/Persistence/AppJsonContext.cs`:

```csharp
using System.Text.Json.Serialization;
using YtDlpTool.Domain.Models;
using YtDlpTool.Domain.Updates;

namespace YtDlpTool.Domain.Persistence;

[JsonSourceGenerationOptions(
    WriteIndented = true,
    Converters = new[] { typeof(JsonStringEnumConverter) },
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AppConfig))]
[JsonSerializable(typeof(StateJournalEvent))]
[JsonSerializable(typeof(JobSnapshot))]
[JsonSerializable(typeof(UpdateManifest))]
[JsonSerializable(typeof(ManifestFileEntry))]
public partial class AppJsonContext : JsonSerializerContext { }
```

- [ ] **Step 3: Build**

```powershell
dotnet build src/YtDlpTool.Domain/
```
Expected: succeeds.

- [ ] **Step 4: Commit**

```powershell
git add src/YtDlpTool.Domain/Updates/ src/YtDlpTool.Domain/Persistence/AppJsonContext.cs
git commit -m "feat(domain): UpdateManifest + ManifestFileEntry types"
```

---

### Task 6.2: GitHub API DTO + HTTP client

**Files:**
- Create: `src/YtDlpTool.Domain/Updates/GitHubReleaseDto.cs`
- Create: `src/YtDlpTool.Domain/Updates/GitHubJsonContext.cs`
- Create: `src/YtDlpTool.Domain/Updates/IUpdateHttpClient.cs`
- Create: `src/YtDlpTool.Domain/Updates/HttpUpdateClient.cs`

We isolate HTTP calls behind an interface so tests can stub network.

- [ ] **Step 1: DTOs**

```csharp
// src/YtDlpTool.Domain/Updates/GitHubReleaseDto.cs
using System.Text.Json.Serialization;

namespace YtDlpTool.Domain.Updates;

public sealed class GitHubReleaseDto
{
    [JsonPropertyName("tag_name")] public string? TagName { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("draft")] public bool Draft { get; set; }
    [JsonPropertyName("prerelease")] public bool Prerelease { get; set; }
    [JsonPropertyName("published_at")] public DateTimeOffset PublishedAt { get; set; }
    [JsonPropertyName("assets")] public List<GitHubAssetDto>? Assets { get; set; }
}

public sealed class GitHubAssetDto
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("browser_download_url")] public string? BrowserDownloadUrl { get; set; }
    [JsonPropertyName("size")] public long Size { get; set; }
}
```

```csharp
// src/YtDlpTool.Domain/Updates/GitHubJsonContext.cs
using System.Text.Json.Serialization;

namespace YtDlpTool.Domain.Updates;

[JsonSerializable(typeof(GitHubReleaseDto))]
public partial class GitHubJsonContext : JsonSerializerContext { }
```

- [ ] **Step 2: HTTP client interface + implementation**

```csharp
// src/YtDlpTool.Domain/Updates/IUpdateHttpClient.cs
namespace YtDlpTool.Domain.Updates;

public interface IUpdateHttpClient
{
    Task<GitHubReleaseDto?> GetLatestReleaseAsync(string owner, string repo, CancellationToken ct);
    Task<string> GetStringAsync(string url, CancellationToken ct);
    Task DownloadAsync(string url, string destPath, IProgress<double>? progress, CancellationToken ct);
}
```

```csharp
// src/YtDlpTool.Domain/Updates/HttpUpdateClient.cs
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
```

- [ ] **Step 3: Build**

```powershell
dotnet build src/YtDlpTool.Domain/
```
Expected: succeeds.

- [ ] **Step 4: Commit**

```powershell
git add src/YtDlpTool.Domain/Updates/
git commit -m "feat(domain): IUpdateHttpClient + HttpUpdateClient (User-Agent, TLS via HttpClient)"
```

---

### Task 6.3: Local version probe (read installed yt-dlp / ffmpeg versions)

**Files:**
- Create: `src/YtDlpTool.Domain/Updates/InstalledVersionProbe.cs`
- Create: `tests/YtDlpTool.Domain.Tests/Updates/InstalledVersionProbeTests.cs`

We need to know what's currently installed to decide if a manifest version is new. We delegate to a callback so tests can stub.

- [ ] **Step 1: Write failing test**

```csharp
// tests/YtDlpTool.Domain.Tests/Updates/InstalledVersionProbeTests.cs
using YtDlpTool.Domain.Updates;

namespace YtDlpTool.Domain.Tests.Updates;

public class InstalledVersionProbeTests
{
    [Fact]
    public void Compare_NewerOnRemote_ReturnsTrue()
    {
        Assert.True(InstalledVersionProbe.IsRemoteNewer("2026.04.01", "2026.05.01"));
        Assert.True(InstalledVersionProbe.IsRemoteNewer("1.2.3", "1.2.4"));
        Assert.True(InstalledVersionProbe.IsRemoteNewer("1.2.3", "2.0.0"));
    }

    [Fact]
    public void Compare_SameOrOlderRemote_ReturnsFalse()
    {
        Assert.False(InstalledVersionProbe.IsRemoteNewer("1.2.3", "1.2.3"));
        Assert.False(InstalledVersionProbe.IsRemoteNewer("1.2.3", "1.2.2"));
        Assert.False(InstalledVersionProbe.IsRemoteNewer("2026.05.01", "2026.04.30"));
    }

    [Fact]
    public void Compare_EmptyLocal_TreatsAsOlder()
    {
        Assert.True(InstalledVersionProbe.IsRemoteNewer("", "1.0.0"));
    }
}
```

- [ ] **Step 2: Implement**

```csharp
// src/YtDlpTool.Domain/Updates/InstalledVersionProbe.cs
namespace YtDlpTool.Domain.Updates;

public static class InstalledVersionProbe
{
    public static bool IsRemoteNewer(string localVersion, string remoteVersion)
    {
        if (string.IsNullOrWhiteSpace(localVersion)) return !string.IsNullOrWhiteSpace(remoteVersion);
        if (string.IsNullOrWhiteSpace(remoteVersion)) return false;
        var localParts = ParseParts(localVersion);
        var remoteParts = ParseParts(remoteVersion);
        var len = Math.Max(localParts.Length, remoteParts.Length);
        for (int i = 0; i < len; i++)
        {
            var l = i < localParts.Length ? localParts[i] : 0;
            var r = i < remoteParts.Length ? remoteParts[i] : 0;
            if (r > l) return true;
            if (r < l) return false;
        }
        return false;
    }

    private static int[] ParseParts(string v)
    {
        var stripped = v.TrimStart('v', 'V');
        return stripped.Split('.', '-')
            .Select(p => int.TryParse(p, out var n) ? n : 0)
            .ToArray();
    }
}
```

- [ ] **Step 3: Run tests**

```powershell
dotnet test tests/YtDlpTool.Domain.Tests/ --filter "FullyQualifiedName~InstalledVersionProbeTests"
```
Expected: 3 pass.

- [ ] **Step 4: Commit**

```powershell
git add src/YtDlpTool.Domain/Updates/InstalledVersionProbe.cs tests/YtDlpTool.Domain.Tests/Updates/InstalledVersionProbeTests.cs
git commit -m "feat(domain): InstalledVersionProbe semver-ish comparison"
```

---

### Task 6.4: `UpdateChecker` — orchestrate three-track check

**Files:**
- Create: `src/YtDlpTool.Domain/Updates/UpdateAvailability.cs`
- Create: `src/YtDlpTool.Domain/Updates/UpdateChecker.cs`
- Create: `tests/YtDlpTool.Domain.Tests/Updates/UpdateCheckerTests.cs`

- [ ] **Step 1: Result type**

```csharp
// src/YtDlpTool.Domain/Updates/UpdateAvailability.cs
namespace YtDlpTool.Domain.Updates;

public sealed record UpdateAvailability(
    bool HasUpdate,
    UpdateManifest? Manifest,
    IReadOnlyList<ManifestFileEntry> NewerFiles,
    string? FailureReason);
```

- [ ] **Step 2: Checker class**

```csharp
// src/YtDlpTool.Domain/Updates/UpdateChecker.cs
using System.Text.Json;
using YtDlpTool.Domain.Persistence;
using YtDlpTool.Domain.Security;

namespace YtDlpTool.Domain.Updates;

public sealed record InstalledVersions(string App, string YtDlp, string Ffmpeg);

public sealed class UpdateChecker
{
    private readonly IUpdateHttpClient _http;
    private readonly SigstoreVerifierOptions _sigstoreOptions;
    private readonly string _owner;
    private readonly string _repo;

    public UpdateChecker(IUpdateHttpClient http, SigstoreVerifierOptions sigstoreOptions, string owner, string repo)
    {
        _http = http;
        _sigstoreOptions = sigstoreOptions;
        _owner = owner;
        _repo = repo;
    }

    public async Task<UpdateAvailability> CheckAsync(
        InstalledVersions installed,
        CancellationToken cancellationToken)
    {
        try
        {
            var release = await _http.GetLatestReleaseAsync(_owner, _repo, cancellationToken).ConfigureAwait(false);
            if (release is null || release.Draft || release.Prerelease || release.Assets is null)
                return new UpdateAvailability(false, null, Array.Empty<ManifestFileEntry>(), "no release");

            var manifestAsset = release.Assets.FirstOrDefault(a => a.Name == "manifest.json");
            var manifestSigAsset = release.Assets.FirstOrDefault(a => a.Name == "manifest.json.sigstore");
            if (manifestAsset?.BrowserDownloadUrl is null || manifestSigAsset?.BrowserDownloadUrl is null)
                return new UpdateAvailability(false, null, Array.Empty<ManifestFileEntry>(), "manifest assets missing");

            var manifestJson = await _http.GetStringAsync(manifestAsset.BrowserDownloadUrl, cancellationToken).ConfigureAwait(false);
            var manifestSigJson = await _http.GetStringAsync(manifestSigAsset.BrowserDownloadUrl, cancellationToken).ConfigureAwait(false);

            var sigVerify = SigstoreVerifier.Verify(
                artifactBytes: System.Text.Encoding.UTF8.GetBytes(manifestJson),
                bundleJson: manifestSigJson,
                options: _sigstoreOptions);

            if (!sigVerify.IsValid)
                return new UpdateAvailability(false, null, Array.Empty<ManifestFileEntry>(), $"簽章驗證失敗：{sigVerify.FailureReason}");

            var manifest = JsonSerializer.Deserialize(manifestJson, AppJsonContext.Default.UpdateManifest);
            if (manifest is null)
                return new UpdateAvailability(false, null, Array.Empty<ManifestFileEntry>(), "manifest 解析失敗");

            var newer = new List<ManifestFileEntry>();
            foreach (var f in manifest.Files)
            {
                var localVersion = f.Component switch
                {
                    UpdateComponent.App => installed.App,
                    UpdateComponent.YtDlp => installed.YtDlp,
                    UpdateComponent.Ffmpeg => installed.Ffmpeg,
                    _ => ""
                };
                if (InstalledVersionProbe.IsRemoteNewer(localVersion, f.Version))
                    newer.Add(f);
            }

            return new UpdateAvailability(newer.Count > 0, manifest, newer, null);
        }
        catch (Exception ex)
        {
            return new UpdateAvailability(false, null, Array.Empty<ManifestFileEntry>(), ex.Message);
        }
    }
}
```

- [ ] **Step 3: Test with fake HTTP**

```csharp
// tests/YtDlpTool.Domain.Tests/Updates/UpdateCheckerTests.cs
using System.Text.Json;
using YtDlpTool.Domain.Persistence;
using YtDlpTool.Domain.Security;
using YtDlpTool.Domain.Updates;

namespace YtDlpTool.Domain.Tests.Updates;

public class UpdateCheckerTests
{
    private static readonly SigstoreVerifierOptions DummyOpts = new(
        ExpectedIssuer: "x",
        ExpectedSanRegex: ".*",
        TrustedRootPem: "");

    private sealed class FakeHttp : IUpdateHttpClient
    {
        public Func<Task<GitHubReleaseDto?>>? OnGetRelease;
        public Dictionary<string, string> Strings = new();

        public Task<GitHubReleaseDto?> GetLatestReleaseAsync(string o, string r, CancellationToken ct) =>
            OnGetRelease?.Invoke() ?? Task.FromResult<GitHubReleaseDto?>(null);
        public Task<string> GetStringAsync(string url, CancellationToken ct) =>
            Task.FromResult(Strings.TryGetValue(url, out var v) ? v : throw new HttpRequestException("404 " + url));
        public Task DownloadAsync(string url, string dest, IProgress<double>? p, CancellationToken ct) =>
            Task.CompletedTask;
    }

    [Fact]
    public async Task Check_NoRelease_ReturnsNoUpdate()
    {
        var http = new FakeHttp { OnGetRelease = () => Task.FromResult<GitHubReleaseDto?>(null) };
        var checker = new UpdateChecker(http, DummyOpts, "o", "r");
        var result = await checker.CheckAsync(new InstalledVersions("1.0.0", "2026.01.01", "7.1"), CancellationToken.None);
        Assert.False(result.HasUpdate);
    }

    [Fact]
    public async Task Check_PrereleaseRelease_IsIgnored()
    {
        var http = new FakeHttp
        {
            OnGetRelease = () => Task.FromResult<GitHubReleaseDto?>(new GitHubReleaseDto
            {
                TagName = "v0.0.1-alpha", Prerelease = true,
                Assets = new() { new GitHubAssetDto { Name = "manifest.json", BrowserDownloadUrl = "x" } }
            })
        };
        var checker = new UpdateChecker(http, DummyOpts, "o", "r");
        var result = await checker.CheckAsync(new InstalledVersions("1.0.0", "2026.01.01", "7.1"), CancellationToken.None);
        Assert.False(result.HasUpdate);
    }

    [Fact]
    public async Task Check_SigstoreFailure_ReturnsFailureReason()
    {
        var manifest = new UpdateManifest
        {
            AppVersion = "2.0.0",
            Files = new() { new ManifestFileEntry { Component = UpdateComponent.App, Version = "2.0.0", Name = "YtDlpTool.exe" } }
        };
        var manifestJson = JsonSerializer.Serialize(manifest, AppJsonContext.Default.UpdateManifest);
        var http = new FakeHttp
        {
            OnGetRelease = () => Task.FromResult<GitHubReleaseDto?>(new GitHubReleaseDto
            {
                TagName = "v2.0.0",
                Assets = new()
                {
                    new GitHubAssetDto { Name = "manifest.json", BrowserDownloadUrl = "https://m" },
                    new GitHubAssetDto { Name = "manifest.json.sigstore", BrowserDownloadUrl = "https://s" }
                }
            }),
            Strings = { ["https://m"] = manifestJson, ["https://s"] = "{invalid}" }
        };
        var checker = new UpdateChecker(http, DummyOpts, "o", "r");
        var result = await checker.CheckAsync(new InstalledVersions("1.0.0", "2026.01.01", "7.1"), CancellationToken.None);
        Assert.False(result.HasUpdate);
        Assert.Contains("簽章", result.FailureReason ?? "");
    }
}
```

- [ ] **Step 4: Run tests**

```powershell
dotnet test tests/YtDlpTool.Domain.Tests/ --filter "FullyQualifiedName~UpdateCheckerTests"
```
Expected: 3 pass.

- [ ] **Step 5: Commit**

```powershell
git add src/YtDlpTool.Domain/Updates/UpdateAvailability.cs src/YtDlpTool.Domain/Updates/UpdateChecker.cs tests/YtDlpTool.Domain.Tests/Updates/UpdateCheckerTests.cs
git commit -m "feat(domain): UpdateChecker — GitHub release + Sigstore-verified manifest"
```

---

### Task 6.5: `UpdateApplier` — atomic download + verify + apply

**Files:**
- Create: `src/YtDlpTool.Domain/Updates/UpdateApplyResult.cs`
- Create: `src/YtDlpTool.Domain/Updates/UpdateApplier.cs`
- Create: `src/YtDlpTool.Domain/Updates/UpdateApplyProgress.cs`
- Create: `tests/YtDlpTool.Domain.Tests/Updates/UpdateApplierTests.cs`

- [ ] **Step 1: Progress + result types**

```csharp
// src/YtDlpTool.Domain/Updates/UpdateApplyProgress.cs
namespace YtDlpTool.Domain.Updates;

public enum UpdateApplyStage { Downloading, VerifyingHash, VerifyingSignature, Applying, Done, RolledBack, Failed }

public sealed record UpdateApplyProgress(
    UpdateApplyStage Stage,
    string FileName,
    double FilePercent,
    int FileIndex,
    int FileCount);
```

```csharp
// src/YtDlpTool.Domain/Updates/UpdateApplyResult.cs
namespace YtDlpTool.Domain.Updates;

public sealed record UpdateApplyResult(bool IsSuccess, string? FailureReason);
```

- [ ] **Step 2: Implement applier**

```csharp
// src/YtDlpTool.Domain/Updates/UpdateApplier.cs
using YtDlpTool.Domain.Persistence;
using YtDlpTool.Domain.Security;

namespace YtDlpTool.Domain.Updates;

public sealed class UpdateApplier
{
    private readonly IUpdateHttpClient _http;
    private readonly SigstoreVerifierOptions _sigstoreOptions;
    private readonly AppPaths _paths;

    public UpdateApplier(IUpdateHttpClient http, SigstoreVerifierOptions sigstoreOptions, AppPaths paths)
    {
        _http = http;
        _sigstoreOptions = sigstoreOptions;
        _paths = paths;
    }

    public async Task<UpdateApplyResult> ApplyAsync(
        IReadOnlyList<ManifestFileEntry> entries,
        IProgress<UpdateApplyProgress>? progress,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_paths.UpdateStaging);

        // Step 1+2: download all + verify hashes
        var staged = new List<(ManifestFileEntry Entry, string StagedPath)>();
        for (int i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            var staged_path = Path.Combine(_paths.UpdateStaging, entry.Name + ".new");
            progress?.Report(new UpdateApplyProgress(UpdateApplyStage.Downloading, entry.Name, 0, i + 1, entries.Count));

            try
            {
                await _http.DownloadAsync(entry.DownloadUrl, staged_path,
                    new Progress<double>(p =>
                        progress?.Report(new UpdateApplyProgress(UpdateApplyStage.Downloading, entry.Name, p, i + 1, entries.Count))),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Cleanup(staged);
                return new UpdateApplyResult(false, $"下載 {entry.Name} 失敗：{ex.Message}");
            }

            progress?.Report(new UpdateApplyProgress(UpdateApplyStage.VerifyingHash, entry.Name, 100, i + 1, entries.Count));
            if (!Sha256Verifier.VerifyFile(staged_path, entry.Sha256))
            {
                Cleanup(staged);
                File.Delete(staged_path);
                return new UpdateApplyResult(false, $"{entry.Name} 內容校驗失敗");
            }

            // Step 3: per-file Sigstore signature (defence in depth)
            progress?.Report(new UpdateApplyProgress(UpdateApplyStage.VerifyingSignature, entry.Name, 100, i + 1, entries.Count));
            try
            {
                var sigJson = await _http.GetStringAsync(entry.SignatureUrl, cancellationToken).ConfigureAwait(false);
                var fileBytes = await File.ReadAllBytesAsync(staged_path, cancellationToken).ConfigureAwait(false);
                var sigResult = SigstoreVerifier.Verify(fileBytes, sigJson, _sigstoreOptions);
                if (!sigResult.IsValid)
                {
                    Cleanup(staged);
                    File.Delete(staged_path);
                    return new UpdateApplyResult(false, $"{entry.Name} 簽章驗證失敗：{sigResult.FailureReason}");
                }
            }
            catch (Exception ex)
            {
                Cleanup(staged);
                File.Delete(staged_path);
                return new UpdateApplyResult(false, $"{entry.Name} 簽章下載失敗：{ex.Message}");
            }

            staged.Add((entry, staged_path));
        }

        // Step 4: atomically replace each file with rollback support
        var applied = new List<(string Live, string Backup)>();
        try
        {
            foreach (var (entry, stagedPath) in staged)
            {
                var livePath = Path.Combine(_paths.AppDirectory, entry.TargetRelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(livePath)!);
                var backupPath = livePath + ".old";

                if (File.Exists(livePath))
                {
                    if (File.Exists(backupPath)) File.Delete(backupPath);
                    File.Move(livePath, backupPath);
                    applied.Add((livePath, backupPath));
                }
                else
                {
                    applied.Add((livePath, ""));
                }
                File.Move(stagedPath, livePath);
                progress?.Report(new UpdateApplyProgress(UpdateApplyStage.Applying, entry.Name, 100, applied.Count, entries.Count));
            }
        }
        catch (Exception ex)
        {
            foreach (var (live, backup) in applied.AsEnumerable().Reverse())
            {
                try
                {
                    if (File.Exists(live)) File.Delete(live);
                    if (!string.IsNullOrEmpty(backup) && File.Exists(backup))
                        File.Move(backup, live);
                }
                catch { }
            }
            progress?.Report(new UpdateApplyProgress(UpdateApplyStage.RolledBack, "", 0, 0, entries.Count));
            return new UpdateApplyResult(false, $"套用失敗已還原：{ex.Message}");
        }

        // Step 5: delete backups
        foreach (var (_, backup) in applied)
            if (!string.IsNullOrEmpty(backup) && File.Exists(backup))
                try { File.Delete(backup); } catch { }

        progress?.Report(new UpdateApplyProgress(UpdateApplyStage.Done, "", 100, entries.Count, entries.Count));
        return new UpdateApplyResult(true, null);
    }

    private static void Cleanup(IEnumerable<(ManifestFileEntry, string)> staged)
    {
        foreach (var (_, p) in staged)
            try { if (File.Exists(p)) File.Delete(p); } catch { }
    }
}
```

- [ ] **Step 3: Test (focus on hash failure & rollback logic; positive path covered by E2E in Phase 10)**

```csharp
// tests/YtDlpTool.Domain.Tests/Updates/UpdateApplierTests.cs
using System.Security.Cryptography;
using YtDlpTool.Domain.Persistence;
using YtDlpTool.Domain.Security;
using YtDlpTool.Domain.Updates;

namespace YtDlpTool.Domain.Tests.Updates;

public class UpdateApplierTests : IDisposable
{
    private readonly string _appDir;
    private readonly AppPaths _paths;

    public UpdateApplierTests()
    {
        _appDir = Path.Combine(Path.GetTempPath(), "ytdlp-app-" + Guid.NewGuid());
        Directory.CreateDirectory(_appDir);
        _paths = AppPaths.ResolveForAppDirectory(_appDir);
        _paths.EnsureDataDirectoriesExist();
    }

    public void Dispose() => Directory.Delete(_appDir, recursive: true);

    private sealed class StubHttp : IUpdateHttpClient
    {
        public Dictionary<string, byte[]> Files = new();
        public Dictionary<string, string> Strings = new();

        public Task<GitHubReleaseDto?> GetLatestReleaseAsync(string o, string r, CancellationToken ct) =>
            Task.FromResult<GitHubReleaseDto?>(null);
        public Task<string> GetStringAsync(string url, CancellationToken ct) =>
            Task.FromResult(Strings[url]);
        public async Task DownloadAsync(string url, string dest, IProgress<double>? p, CancellationToken ct)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            await File.WriteAllBytesAsync(dest, Files[url], ct);
            p?.Report(100);
        }
    }

    [Fact]
    public async Task Apply_HashMismatch_AbortsAndCleansUp()
    {
        var http = new StubHttp();
        var content = System.Text.Encoding.UTF8.GetBytes("totally legit");
        http.Files["https://fake/yt-dlp.exe"] = content;
        http.Strings["https://fake/yt-dlp.exe.sigstore"] = "{}";

        var entry = new ManifestFileEntry
        {
            Name = "yt-dlp.exe",
            Component = UpdateComponent.YtDlp,
            DownloadUrl = "https://fake/yt-dlp.exe",
            SignatureUrl = "https://fake/yt-dlp.exe.sigstore",
            Sha256 = new string('0', 64), // intentionally wrong
            TargetRelativePath = "bin\\yt-dlp.exe",
            Version = "2026.05.14"
        };

        var applier = new UpdateApplier(http,
            new SigstoreVerifierOptions("x", ".*", ""), _paths);
        var result = await applier.ApplyAsync(new[] { entry }, null, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains("校驗失敗", result.FailureReason!);
        Assert.False(File.Exists(Path.Combine(_paths.UpdateStaging, "yt-dlp.exe.new")));
    }

    [Fact]
    public async Task Apply_SigFailure_AbortsAfterHash()
    {
        var http = new StubHttp();
        var content = System.Text.Encoding.UTF8.GetBytes("totally legit");
        var sha = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        http.Files["https://fake/yt-dlp.exe"] = content;
        http.Strings["https://fake/yt-dlp.exe.sigstore"] = "{invalid sigstore bundle}";

        var entry = new ManifestFileEntry
        {
            Name = "yt-dlp.exe",
            Component = UpdateComponent.YtDlp,
            DownloadUrl = "https://fake/yt-dlp.exe",
            SignatureUrl = "https://fake/yt-dlp.exe.sigstore",
            Sha256 = sha,
            TargetRelativePath = "bin\\yt-dlp.exe",
            Version = "2026.05.14"
        };

        var applier = new UpdateApplier(http,
            new SigstoreVerifierOptions("x", ".*", ""), _paths);
        var result = await applier.ApplyAsync(new[] { entry }, null, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains("簽章", result.FailureReason!);
    }
}
```

- [ ] **Step 4: Run**

```powershell
dotnet test tests/YtDlpTool.Domain.Tests/ --filter "FullyQualifiedName~UpdateApplierTests"
```
Expected: 2 pass.

- [ ] **Step 5: Commit**

```powershell
git add src/YtDlpTool.Domain/Updates/UpdateApplyProgress.cs src/YtDlpTool.Domain/Updates/UpdateApplyResult.cs src/YtDlpTool.Domain/Updates/UpdateApplier.cs tests/YtDlpTool.Domain.Tests/Updates/UpdateApplierTests.cs
git commit -m "feat(domain): UpdateApplier with hash+sig verify, atomic rename, rollback on failure"
```

---

### Task 6.6: Full suite + AOT

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
git tag phase-6-update-complete
```

---

## Phase 6 complete gate

- [ ] Manifest types + JSON context
- [ ] `IUpdateHttpClient` + `HttpUpdateClient`
- [ ] `InstalledVersionProbe` with comparison tests
- [ ] `UpdateChecker` integrates GitHub API + Sigstore verify
- [ ] `UpdateApplier` performs download → hash → sig → atomic rename → rollback
- [ ] AOT publish green
- [ ] Tag `phase-6-update-complete`

Proceed to Phase 7.

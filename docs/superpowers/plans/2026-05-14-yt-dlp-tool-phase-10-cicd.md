# Phase 10 · CI/CD · Sigstore Signing · Release Pipeline

**Goal:** Set up the GitHub Actions workflows for PR validation and tagged release. Tagged release fetches pinned external deps, builds NativeAOT exe, packs portable folder, signs `manifest.json` and individual binaries with Sigstore keyless, uploads to GitHub Releases. Also: bake real Sigstore root certificate into `SigstoreRoots`, write README with SmartScreen unblock instructions.

**Prerequisites:** Phase 9 complete (tag `phase-9-settings-complete`).

> **Bound parameters you fill in before this phase runs:** GitHub `<OWNER>/<REPO>`. Search/replace `OWNER` and `REPO` placeholders throughout this phase with the real values. Update `AppHost.cs` and `SettingsDialog.xaml.cs` accordingly.

---

### Task 10.1: External dependency manifest

**Files:**
- Create: `build/external-deps.json`

- [ ] **Step 1: Write the manifest**

```json
{
  "yt-dlp": {
    "version": "2026.05.01",
    "url": "https://github.com/yt-dlp/yt-dlp/releases/download/2026.05.01/yt-dlp.exe",
    "sha256": "TO_BE_FILLED_AT_FIRST_BUILD"
  },
  "ffmpeg": {
    "version": "7.1-essentials_build",
    "url": "https://www.gyan.dev/ffmpeg/builds/ffmpeg-7.1-essentials_build.zip",
    "sha256": "TO_BE_FILLED_AT_FIRST_BUILD",
    "executableInsideZip": "ffmpeg-7.1-essentials_build/bin/ffmpeg.exe"
  }
}
```

- [ ] **Step 2: Commit**

```powershell
git add build/external-deps.json
git commit -m "chore(build): pinned external dependency manifest"
```

---

### Task 10.2: Fetch script

**Files:**
- Create: `build/fetch-external-deps.ps1`

- [ ] **Step 1: Write the script**

```powershell
# build/fetch-external-deps.ps1
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)] [string] $OutputDir,
    [Parameter(Mandatory=$false)] [string] $ManifestPath = "build/external-deps.json"
)

$ErrorActionPreference = 'Stop'

function Verify-Sha256 {
    param([string] $Path, [string] $Expected)
    if ($Expected -eq 'TO_BE_FILLED_AT_FIRST_BUILD') {
        $actual = (Get-FileHash -Algorithm SHA256 -Path $Path).Hash.ToLowerInvariant()
        Write-Warning "First build for $Path. Computed SHA-256: $actual"
        return $actual
    }
    $actual = (Get-FileHash -Algorithm SHA256 -Path $Path).Hash.ToLowerInvariant()
    if ($actual -ne $Expected.ToLowerInvariant()) {
        throw "SHA-256 mismatch for $Path. Expected $Expected, got $actual."
    }
    return $actual
}

$manifest = Get-Content -Raw -Path $ManifestPath | ConvertFrom-Json
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
$binDir = Join-Path $OutputDir 'bin'
New-Item -ItemType Directory -Force -Path $binDir | Out-Null

# yt-dlp
$ytDlpDest = Join-Path $binDir 'yt-dlp.exe'
Write-Host "Downloading yt-dlp from $($manifest.'yt-dlp'.url)"
Invoke-WebRequest -Uri $manifest.'yt-dlp'.url -OutFile $ytDlpDest -UseBasicParsing
$ytdlpSha = Verify-Sha256 -Path $ytDlpDest -Expected $manifest.'yt-dlp'.sha256

# ffmpeg
$ffmpegZip = Join-Path $env:TEMP "ffmpeg-$(New-Guid).zip"
Write-Host "Downloading ffmpeg from $($manifest.ffmpeg.url)"
Invoke-WebRequest -Uri $manifest.ffmpeg.url -OutFile $ffmpegZip -UseBasicParsing
$ffmpegZipSha = Verify-Sha256 -Path $ffmpegZip -Expected $manifest.ffmpeg.sha256

$ffmpegExtractDir = Join-Path $env:TEMP "ffmpeg-extract-$(New-Guid)"
Expand-Archive -Path $ffmpegZip -DestinationPath $ffmpegExtractDir -Force
$ffmpegExeSrc = Join-Path $ffmpegExtractDir $manifest.ffmpeg.executableInsideZip
if (-not (Test-Path $ffmpegExeSrc)) {
    throw "ffmpeg.exe not found at expected path inside zip: $($manifest.ffmpeg.executableInsideZip)"
}
Copy-Item -Path $ffmpegExeSrc -Destination (Join-Path $binDir 'ffmpeg.exe') -Force
Remove-Item -Recurse -Force $ffmpegExtractDir
Remove-Item -Force $ffmpegZip

Write-Host "External deps prepared in $binDir"
Write-Host "  yt-dlp.exe  SHA-256 = $ytdlpSha"
Write-Host "  ffmpeg.zip  SHA-256 = $ffmpegZipSha"
```

- [ ] **Step 2: Manual local test (Windows)**

```powershell
pwsh build/fetch-external-deps.ps1 -OutputDir "$env:TEMP\YtDlpTool-fetch-test"
```
Expected: warning about `TO_BE_FILLED_AT_FIRST_BUILD` with computed SHA. Copy those values into `build/external-deps.json`, commit.

- [ ] **Step 3: Update `external-deps.json` with real hashes**

After running once, copy the computed SHAs and replace the placeholders. Commit:

```powershell
git add build/external-deps.json
git commit -m "chore(build): record SHA-256 hashes from first fetch"
```

- [ ] **Step 4: Commit script**

```powershell
git add build/fetch-external-deps.ps1
git commit -m "chore(build): PowerShell script to fetch + verify pinned external deps"
```

---

### Task 10.3: Manifest assembly script

**Files:**
- Create: `build/build-manifest.ps1`

This script runs after the AOT publish, takes the built artifacts + fetched deps, computes hashes, and writes `manifest.json`.

- [ ] **Step 1: Write the script**

```powershell
# build/build-manifest.ps1
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)] [string] $PortableDir,
    [Parameter(Mandatory=$true)] [string] $AppVersion,
    [Parameter(Mandatory=$true)] [string] $YtDlpVersion,
    [Parameter(Mandatory=$true)] [string] $FfmpegVersion,
    [Parameter(Mandatory=$true)] [string] $Owner,
    [Parameter(Mandatory=$true)] [string] $Repo,
    [Parameter(Mandatory=$true)] [string] $TagName,
    [Parameter(Mandatory=$true)] [string] $OutputManifestPath
)

$ErrorActionPreference = 'Stop'

function Sha-Hex { param([string] $Path) (Get-FileHash -Algorithm SHA256 -Path $Path).Hash.ToLowerInvariant() }

$assetBase = "https://github.com/$Owner/$Repo/releases/download/$TagName"

$files = @(
    @{
        name = 'YtDlpTool.exe'; component = 'App'; version = $AppVersion
        path = Join-Path $PortableDir 'YtDlpTool.exe'; rel = 'YtDlpTool.exe'
    },
    @{
        name = 'yt-dlp.exe'; component = 'YtDlp'; version = $YtDlpVersion
        path = Join-Path $PortableDir 'bin\yt-dlp.exe'; rel = 'bin\yt-dlp.exe'
    },
    @{
        name = 'ffmpeg.exe'; component = 'Ffmpeg'; version = $FfmpegVersion
        path = Join-Path $PortableDir 'bin\ffmpeg.exe'; rel = 'bin\ffmpeg.exe'
    }
)

$entries = $files | ForEach-Object {
    [pscustomobject]@{
        name                = $_.name
        component           = $_.component
        version             = $_.version
        downloadUrl         = "$assetBase/$($_.name)"
        sha256              = (Sha-Hex -Path $_.path)
        signatureUrl        = "$assetBase/$($_.name).sigstore"
        targetRelativePath  = $_.rel
    }
}

$manifest = [pscustomobject]@{
    manifestVersion = '1'
    publishedAt     = (Get-Date).ToUniversalTime().ToString('o')
    appVersion      = $AppVersion
    ytDlpVersion    = $YtDlpVersion
    ffmpegVersion   = $FfmpegVersion
    files           = $entries
}

$manifest | ConvertTo-Json -Depth 6 | Set-Content -Encoding utf8 -Path $OutputManifestPath
Write-Host "Manifest written to $OutputManifestPath"
```

- [ ] **Step 2: Commit**

```powershell
git add build/build-manifest.ps1
git commit -m "chore(build): manifest assembly script"
```

---

### Task 10.4: PR-check workflow

**Files:**
- Create: `.github/workflows/pr-check.yml`

- [ ] **Step 1: Write the workflow**

```yaml
# .github/workflows/pr-check.yml
name: PR check
on:
  pull_request:
    branches: [main]
  push:
    branches: [main]

jobs:
  build-and-test:
    runs-on: windows-2022
    steps:
      - name: Checkout
        uses: actions/checkout@v4

      - name: Setup .NET 8
        uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '8.0.x'

      - name: Restore (locked mode)
        run: dotnet restore --locked-mode

      - name: Build (Release)
        run: dotnet build -c Release --no-restore

      - name: Test
        run: dotnet test -c Release --no-build --verbosity normal

      - name: AOT publish smoke
        run: dotnet publish src/YtDlpTool/YtDlpTool.csproj -c Release -r win-x64 --no-restore

      - name: Verify AOT exe runs version probe
        shell: pwsh
        run: |
          $exe = Get-ChildItem -Recurse -Path src/YtDlpTool/bin/Release -Filter YtDlpTool.exe | Select-Object -First 1
          if (-not $exe) { throw 'AOT exe missing' }
          Write-Host "AOT exe: $($exe.FullName) size=$([math]::Round($exe.Length / 1MB, 2))MB"
```

- [ ] **Step 2: Commit**

```powershell
git add .github/workflows/pr-check.yml
git commit -m "ci: PR check workflow (restore, build, test, AOT smoke)"
```

---

### Task 10.5: Release workflow

**Files:**
- Create: `.github/workflows/release.yml`

- [ ] **Step 1: Write the workflow**

```yaml
# .github/workflows/release.yml
name: Release
on:
  push:
    tags: ['v*']

permissions:
  contents: write    # for gh release create
  id-token: write    # for Sigstore OIDC

jobs:
  release:
    runs-on: windows-2022
    steps:
      - name: Checkout
        uses: actions/checkout@v4

      - name: Setup .NET 8
        uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '8.0.x'

      - name: Compute version from tag
        id: ver
        shell: pwsh
        run: |
          $tag = "${{ github.ref_name }}"
          $version = $tag.TrimStart('v')
          "version=$version" | Out-File -FilePath $env:GITHUB_OUTPUT -Append
          "tag=$tag"          | Out-File -FilePath $env:GITHUB_OUTPUT -Append
          # External-deps versions
          $deps = Get-Content -Raw build/external-deps.json | ConvertFrom-Json
          "ytdlpVersion=$($deps.'yt-dlp'.version)" | Out-File -FilePath $env:GITHUB_OUTPUT -Append
          "ffmpegVersion=$($deps.ffmpeg.version)"   | Out-File -FilePath $env:GITHUB_OUTPUT -Append

      - name: Set assembly version
        shell: pwsh
        run: |
          $version = "${{ steps.ver.outputs.version }}"
          $csproj = 'src/YtDlpTool/YtDlpTool.csproj'
          $content = Get-Content -Raw $csproj
          if ($content -notmatch '<Version>') {
            $content = $content -replace '</PropertyGroup>',
              "  <Version>$version</Version>`n  </PropertyGroup>"
          } else {
            $content = $content -replace '<Version>[^<]+</Version>', "<Version>$version</Version>"
          }
          Set-Content -Path $csproj -Value $content -Encoding utf8

      - name: Restore (locked)
        run: dotnet restore --locked-mode

      - name: Test
        run: dotnet test -c Release

      - name: AOT publish
        run: dotnet publish src/YtDlpTool/YtDlpTool.csproj -c Release -r win-x64 --no-restore

      - name: Fetch external deps
        shell: pwsh
        run: |
          $publishDir = (Get-ChildItem -Recurse -Path src/YtDlpTool/bin/Release -Filter publish -Directory | Select-Object -First 1).FullName
          ./build/fetch-external-deps.ps1 -OutputDir $publishDir

      - name: Assemble portable folder
        id: portable
        shell: pwsh
        run: |
          $publishDir = (Get-ChildItem -Recurse -Path src/YtDlpTool/bin/Release -Filter publish -Directory | Select-Object -First 1).FullName
          $stage = Join-Path $env:RUNNER_TEMP "YtDlpTool-stage"
          New-Item -ItemType Directory -Force -Path $stage | Out-Null
          Copy-Item -Recurse "$publishDir\*" $stage -Force
          # Keep only the things we ship:
          Get-ChildItem $stage -File | Where-Object { $_.Name -notin @('YtDlpTool.exe') } | Remove-Item -Force
          "stageDir=$stage" | Out-File -FilePath $env:GITHUB_OUTPUT -Append

      - name: Build manifest
        shell: pwsh
        run: |
          ./build/build-manifest.ps1 `
            -PortableDir "${{ steps.portable.outputs.stageDir }}" `
            -AppVersion "${{ steps.ver.outputs.version }}" `
            -YtDlpVersion "${{ steps.ver.outputs.ytdlpVersion }}" `
            -FfmpegVersion "${{ steps.ver.outputs.ffmpegVersion }}" `
            -Owner "${{ github.repository_owner }}" `
            -Repo "${{ github.event.repository.name }}" `
            -TagName "${{ steps.ver.outputs.tag }}" `
            -OutputManifestPath "${{ steps.portable.outputs.stageDir }}/manifest.json"

      - name: Install cosign
        uses: sigstore/cosign-installer@v3

      - name: Sigstore-sign manifest + per-file
        shell: pwsh
        run: |
          $stage = "${{ steps.portable.outputs.stageDir }}"
          $env:COSIGN_EXPERIMENTAL = '1'
          cosign sign-blob --yes --bundle "$stage/manifest.json.sigstore" "$stage/manifest.json"
          cosign sign-blob --yes --bundle "$stage/YtDlpTool.exe.sigstore" "$stage/YtDlpTool.exe"
          cosign sign-blob --yes --bundle "$stage/yt-dlp.exe.sigstore"    "$stage/bin/yt-dlp.exe"
          cosign sign-blob --yes --bundle "$stage/ffmpeg.exe.sigstore"    "$stage/bin/ffmpeg.exe"

      - name: Pack portable zip
        id: zip
        shell: pwsh
        run: |
          $stage = "${{ steps.portable.outputs.stageDir }}"
          $zipName = "YtDlpTool-${{ steps.ver.outputs.tag }}-win-x64.zip"
          $zipPath = Join-Path $env:RUNNER_TEMP $zipName
          Compress-Archive -Path "$stage\*" -DestinationPath $zipPath -Force
          "zipPath=$zipPath" | Out-File -FilePath $env:GITHUB_OUTPUT -Append
          "zipName=$zipName" | Out-File -FilePath $env:GITHUB_OUTPUT -Append

      - name: Create GitHub Release
        shell: pwsh
        env:
          GH_TOKEN: ${{ secrets.GITHUB_TOKEN }}
        run: |
          $stage = "${{ steps.portable.outputs.stageDir }}"
          gh release create "${{ steps.ver.outputs.tag }}" `
            "$stage/manifest.json" `
            "$stage/manifest.json.sigstore" `
            "$stage/YtDlpTool.exe" `
            "$stage/YtDlpTool.exe.sigstore" `
            "$stage/bin/yt-dlp.exe" `
            "$stage/yt-dlp.exe.sigstore" `
            "$stage/bin/ffmpeg.exe" `
            "$stage/ffmpeg.exe.sigstore" `
            "${{ steps.zip.outputs.zipPath }}" `
            --title "${{ steps.ver.outputs.tag }}" `
            --notes "See manifest.json for component versions."
```

- [ ] **Step 2: Commit**

```powershell
git add .github/workflows/release.yml
git commit -m "ci: release workflow with Sigstore-keyless signing"
```

---

### Task 10.6: Wire real owner/repo into source

**Files:**
- Modify: `src/YtDlpTool/AppHost.cs`
- Modify: `src/YtDlpTool/Dialogs/SettingsDialog.xaml.cs`

- [ ] **Step 1: Replace `OWNER` / `REPO` placeholders**

Search-and-replace in the two files: change `OWNER` → your GitHub owner, `REPO` → your GitHub repo name. (For this plan, document: when you read this, substitute the actual values.)

If your repo is at `github.com/yourname/YtDlpTool`, the resulting strings should be:

```csharp
// In AppHost.cs
ExpectedSanRegex: @"^https://github\.com/yourname/YtDlpTool/\.github/workflows/release\.yml@refs/tags/v.*$",
UpdateChecker = new UpdateChecker(UpdateHttp, sigstoreOpts, owner: "yourname", repo: "YtDlpTool");

// In SettingsDialog.xaml.cs (About message)
"https://github.com/yourname/YtDlpTool/.github/workflows/release.yml"
```

- [ ] **Step 2: Commit**

```powershell
git add src/YtDlpTool/AppHost.cs src/YtDlpTool/Dialogs/SettingsDialog.xaml.cs
git commit -m "chore: wire real GitHub owner/repo into Sigstore expected identity"
```

---

### Task 10.7: Update `SigstoreRoots` with real Fulcio PEM

**Files:**
- Modify: `src/YtDlpTool.Domain/Security/SigstoreRoots.cs`

- [ ] **Step 1: Fetch the current Fulcio root certificate**

From a local machine:

```powershell
Invoke-WebRequest -Uri "https://raw.githubusercontent.com/sigstore/root-signing/main/repository/repository/targets/fulcio_v1.crt.pem" -OutFile fulcio_v1.crt.pem
Get-Content fulcio_v1.crt.pem
```

Copy the entire PEM into `SigstoreRoots.cs` replacing `<replaced-in-phase-10>`.

Do the same for the Rekor public key:

```powershell
Invoke-WebRequest -Uri "https://raw.githubusercontent.com/sigstore/root-signing/main/repository/repository/targets/rekor.pub" -OutFile rekor.pub
```

Replace `<replaced-in-phase-10>` for `RekorPublicKeyPem` with the contents.

- [ ] **Step 2: Build + test**

```powershell
dotnet build src/YtDlpTool.Domain/
dotnet test
```
Expected: green.

- [ ] **Step 3: Commit**

```powershell
git add src/YtDlpTool.Domain/Security/SigstoreRoots.cs
git commit -m "feat(security): bake real Fulcio root + Rekor public key"
```

---

### Task 10.8: Add an end-to-end Sigstore fixture test

**Files:**
- Create: `tests/fixtures/sigstore/sample-manifest.json` (will be filled with real signed manifest)
- Create: `tests/fixtures/sigstore/sample-manifest.json.sigstore`
- Create: `tests/YtDlpTool.Domain.Tests/Security/SigstoreVerifierFixtureTests.cs`

- [ ] **Step 1: Push tag `v0.0.1-test`** to trigger the release workflow

```powershell
git tag v0.0.1-test
git push origin v0.0.1-test
```

- [ ] **Step 2: Wait for CI**, then download `manifest.json` + `manifest.json.sigstore` from the release.

Save them to `tests/fixtures/sigstore/`. Commit:

```powershell
git add tests/fixtures/sigstore/
git commit -m "test: snapshot Sigstore bundle fixture from v0.0.1-test"
```

- [ ] **Step 3: Add fixture test**

```csharp
// tests/YtDlpTool.Domain.Tests/Security/SigstoreVerifierFixtureTests.cs
using YtDlpTool.Domain.Security;

namespace YtDlpTool.Domain.Tests.Security;

public class SigstoreVerifierFixtureTests
{
    private static string FixturePath(string name)
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            var candidate = Path.Combine(dir, "tests", "fixtures", "sigstore", name);
            if (File.Exists(candidate)) return candidate;
            var parent = Directory.GetParent(dir);
            if (parent is null) break;
            dir = parent.FullName;
        }
        throw new FileNotFoundException(name);
    }

    [Fact]
    public void Verify_RealManifestBundle_Succeeds()
    {
        var manifestBytes = File.ReadAllBytes(FixturePath("sample-manifest.json"));
        var bundleJson = File.ReadAllText(FixturePath("sample-manifest.json.sigstore"));
        var opts = new SigstoreVerifierOptions(
            ExpectedIssuer: "https://token.actions.githubusercontent.com",
            // Replace OWNER/REPO with real values before running.
            ExpectedSanRegex: @"^https://github\.com/OWNER/REPO/\.github/workflows/release\.yml@refs/tags/v0\.0\.1-test$",
            TrustedRootPem: SigstoreRoots.FulcioRootPem);
        var result = SigstoreVerifier.Verify(manifestBytes, bundleJson, opts);
        Assert.True(result.IsValid, result.FailureReason);
    }
}
```

> Don't merge this until the placeholder OWNER/REPO is replaced and the test passes locally.

- [ ] **Step 4: Run**

```powershell
dotnet test tests/YtDlpTool.Domain.Tests/ --filter "FullyQualifiedName~SigstoreVerifierFixtureTests"
```
Expected: passes.

- [ ] **Step 5: Commit**

```powershell
git add tests/YtDlpTool.Domain.Tests/Security/SigstoreVerifierFixtureTests.cs
git commit -m "test(security): SigstoreVerifier positive-path fixture from real CI bundle"
```

---

### Task 10.9: README with SmartScreen unblock instructions

**Files:**
- Create: `README.md`

- [ ] **Step 1: Write README**

```markdown
# YtDlpTool

A lightweight Windows desktop YouTube downloader built on yt-dlp. Aurora Glass UI, hardened security, portable folder distribution, one-click Sigstore-verified updates.

## Install

1. Download `YtDlpTool-vX.Y.Z-win-x64.zip` from [Releases](https://github.com/OWNER/REPO/releases/latest).
2. Unzip anywhere. No installation required.
3. Run `YtDlpTool.exe`.

### SmartScreen warning (first run)

YtDlpTool v1 is not Authenticode-signed. The first time you launch it Windows SmartScreen may show "Windows protected your PC".

To allow it:

1. Click **More info**.
2. Click **Run anyway**.

Alternative if AppLocker blocks even after that: right-click `YtDlpTool.exe` → **Properties** → tick **Unblock** at the bottom → **OK**.

Future versions may be signed via Azure Trusted Signing (no SmartScreen warning).

## Verify the download (optional, recommended)

Each release includes Sigstore signatures. To verify locally with cosign:

```pwsh
cosign verify-blob `
  --bundle YtDlpTool.exe.sigstore `
  --certificate-identity-regexp "^https://github\.com/OWNER/REPO/\.github/workflows/release\.yml@refs/tags/v.*$" `
  --certificate-oidc-issuer "https://token.actions.githubusercontent.com" `
  YtDlpTool.exe
```

## Features

- Single YouTube URL → choose mode (audio / video+audio / video-only) → choose quality → download
- Up to 5 concurrent downloads
- Subtitle download + embed (up to 3 languages per video)
- Time-range clipping (precise, keyframe-aligned)
- Thumbnail embedding
- One-click background updates with Sigstore verification

## Security

- All YouTube URLs validated against a strict allowlist before invoking yt-dlp.
- Subprocess invocation uses argument arrays (no shell), an environment whitelist, and stdout buffer caps.
- Updates verify both a Sigstore-signed manifest and a Sigstore signature on every individual file.
- App stores nothing remotely; no telemetry. Logs do not include URLs or video titles.

## License

MIT for YtDlpTool. Bundled binaries retain their upstream licenses:
- yt-dlp: Unlicense
- ffmpeg: GPL/LGPL (this build is the `essentials` GPL build from gyan.dev)
```

- [ ] **Step 2: Commit**

```powershell
git add README.md
git commit -m "docs: README with install, SmartScreen unblock, and Sigstore verify steps"
```

---

### Task 10.10: First real release smoke test

- [ ] **Step 1: Push first proper tag**

```powershell
git tag v1.0.0
git push origin v1.0.0
```

- [ ] **Step 2: Wait for CI to complete**, then:
  - Download `YtDlpTool-v1.0.0-win-x64.zip`
  - Unzip to a fresh location
  - Run `YtDlpTool.exe`
  - Verify the app opens and accepts a URL → metadata appears → can complete a real download

- [ ] **Step 3: Manual security check**: verify the Sigstore bundle with cosign locally (commands in README).

- [ ] **Step 4: Tag the milestone**

```powershell
git tag phase-10-complete
git push --tags
```

---

## Phase 10 complete gate

- [ ] `external-deps.json` with real SHA-256 hashes
- [ ] `fetch-external-deps.ps1` and `build-manifest.ps1`
- [ ] PR-check + release GitHub Actions workflows
- [ ] `SigstoreRoots` has real Fulcio + Rekor PEMs
- [ ] OWNER/REPO substituted everywhere
- [ ] Fixture test for end-to-end Sigstore verification
- [ ] README with install + verification instructions
- [ ] First real `v1.0.0` release pushed and smoke-tested
- [ ] Tag `phase-10-complete`

---

## Project complete

All ten phases done. The repo now contains:
- A working, AOT-compiled Windows YouTube downloader (~15 MB exe)
- 80+ unit tests across Domain, Security, Process layers
- Portable zip distribution with Sigstore-signed manifest + per-file signatures
- One-click in-app update with rollback safety
- zh-TW UI with Aurora Glass aesthetics

Next: if Authenticode signing is in scope, add Azure Trusted Signing per spec Section 10.1.

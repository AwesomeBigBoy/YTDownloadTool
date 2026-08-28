# build/build-patched-ytdlp.ps1
#
# Builds our patched yt-dlp.exe from upstream source.
#
# Reads the pinned upstream commit and source-archive SHA256 from
# external-deps.json. Downloads the source at that commit, verifies the
# archive integrity, installs PyInstaller, applies our runtime hook, and
# produces a yt-dlp.exe that lowers OpenSSL SECLEVEL only when the env var
# YTDLP_RELAX_SECLEVEL=1 is set (otherwise behaves identically to upstream).
#
# Inputs:
#   -OutputDir <path>      bin/ folder where yt-dlp.exe should land
#   -ManifestPath <path>   external-deps.json (default: build/external-deps.json)
#
# Output:
#   <OutputDir>/bin/yt-dlp.exe

[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)] [string] $OutputDir,
    [Parameter(Mandatory=$false)] [string] $ManifestPath = "build/external-deps.json"
)

$ErrorActionPreference = 'Stop'

# ─── 1. Read pinned upstream info ─────────────────────────────────────────
$manifest = Get-Content -Raw -Path $ManifestPath | ConvertFrom-Json
$upstreamTag     = $manifest.'yt-dlp'.upstream_tag
$expectedZipSha  = $manifest.'yt-dlp'.upstream_source_zip_sha256

if (-not $upstreamTag) {
    throw "external-deps.json missing yt-dlp.upstream_tag"
}

Write-Host "yt-dlp upstream tag: $upstreamTag"

# ─── 2. Download upstream source archive (pinned by tag) ─────────────────
# We use the tag URL — GitHub serves an archive of the commit that tag
# points at. The SHA256 we verify against upstream_source_zip_sha256
# catches any change (theoretical tag move, archive format change, etc.).
$srcUrl = "https://github.com/yt-dlp/yt-dlp/archive/refs/tags/$upstreamTag.zip"
$srcZip = Join-Path $env:TEMP "yt-dlp-source-$upstreamTag.zip"
Write-Host "Downloading: $srcUrl"
Invoke-WebRequest -Uri $srcUrl -OutFile $srcZip -UseBasicParsing

$actualSha = (Get-FileHash -Algorithm SHA256 -Path $srcZip).Hash.ToLowerInvariant()
if ($expectedZipSha -eq 'TO_BE_FILLED_AT_FIRST_BUILD') {
    Write-Warning "First build — record this in external-deps.json:"
    Write-Warning "  upstream_source_zip_sha256: $actualSha"
} elseif ($actualSha -ne $expectedZipSha.ToLowerInvariant()) {
    throw "Source archive SHA256 mismatch. Expected $expectedZipSha, got $actualSha."
}
Write-Host "Source archive SHA256: $actualSha"

# ─── 3. Extract ──────────────────────────────────────────────────────────
$srcDir = Join-Path $env:TEMP "yt-dlp-source-extract-$upstreamTag"
if (Test-Path $srcDir) { Remove-Item -Recurse -Force $srcDir }
Expand-Archive -Path $srcZip -DestinationPath $srcDir -Force

$ytDlpRoot = (Get-ChildItem $srcDir -Directory | Select-Object -First 1).FullName
if (-not $ytDlpRoot) { throw "Source archive had no top-level directory" }
Write-Host "Source extracted to: $ytDlpRoot"

# ─── 4. Copy runtime hook into source tree ───────────────────────────────
$hookSrc  = Join-Path (Split-Path -Parent $PSCommandPath) 'yt-dlp-patch/ytdlp_seclevel_hook.py'
$hookDest = Join-Path $ytDlpRoot 'ytdlp_seclevel_hook.py'
Copy-Item $hookSrc $hookDest -Force
Write-Host "Runtime hook copied: $hookDest"

# ─── 5. Install build dependencies ───────────────────────────────────────
Write-Host "Upgrading pip..."
& python -m pip install --upgrade pip 2>&1 | Out-Null

Write-Host "Installing PyInstaller..."
& python -m pip install pyinstaller 2>&1 | Out-Null

# yt-dlp's build deps. Try the canonical path first; fall back to source if missing.
$reqFile = Join-Path $ytDlpRoot 'requirements.txt'
if (Test-Path $reqFile) {
    Write-Host "Installing yt-dlp requirements.txt"
    & python -m pip install -r $reqFile 2>&1 | Out-Null
} else {
    Write-Host "No requirements.txt at root — installing yt-dlp's core runtime deps directly"
    & python -m pip install brotli certifi mutagen pycryptodomex requests websockets urllib3 2>&1 | Out-Null
}

# Install yt-dlp itself in editable mode so its package metadata resolves
Push-Location $ytDlpRoot
try {
    & python -m pip install -e . 2>&1 | Out-Null
} finally {
    Pop-Location
}

# ─── 6. Build with PyInstaller + our runtime hook ────────────────────────
Push-Location $ytDlpRoot
try {
    Write-Host "Running PyInstaller..."
    # yt-dlp's entry module is yt_dlp/__main__.py. --runtime-hook injects our
    # hook so it runs before yt-dlp imports ssl.
    #
    # v1.4.0: --onedir, NOT --onefile. An onefile binary re-extracts its entire
    # ~40 MB bundle into a brand-new %TEMP%\_MEIxxxxxx on EVERY run and deletes
    # it on exit, so antivirus rescans the whole payload every single time and
    # nothing ever warms up. Two field machines in 2026-08 measured 18-24s of
    # that before yt-dlp executed a line of its own code, which on a 30s budget
    # left 6-12s for actual network work — enough on a plain network, not enough
    # behind an HTTPS-inspecting proxy. An onedir build has nothing to extract
    # and starts in milliseconds.
    & python -m PyInstaller `
        --runtime-hook ytdlp_seclevel_hook.py `
        --name yt-dlp `
        --onedir `
        --console `
        --collect-all yt_dlp `
        --noconfirm `
        yt_dlp/__main__.py 2>&1 | Tee-Object -Variable pyinstallerOutput | Out-Null

    $builtDir = Join-Path $ytDlpRoot 'dist/yt-dlp'
    $builtExe = Join-Path $builtDir 'yt-dlp.exe'
    if (-not (Test-Path $builtExe)) {
        Write-Host "PyInstaller output:"
        $pyinstallerOutput | ForEach-Object { Write-Host "  $_" }
        throw "PyInstaller did not produce dist/yt-dlp/yt-dlp.exe"
    }
} finally {
    Pop-Location
}

# ─── 7. Copy to OutputDir/bin and report ─────────────────────────────────
# v1.4.0: the whole dist/yt-dlp/ directory ships, and is ALSO zipped as
# bin/yt-dlp-pkg.zip. The zip is what the in-app updater delivers: it is an
# ordinary manifest entry that UpdateApplier downloads, hash-checks, signature-
# verifies and drops in place with no knowledge that it is special, so the
# update path needed no changes at all. The app expands it on next launch —
# see src/YtDlpTool.Process/YtDlpLayout.cs.
$binDir = Join-Path $OutputDir 'bin'
New-Item -ItemType Directory -Force -Path $binDir | Out-Null

$destDir = Join-Path $binDir 'yt-dlp'
if (Test-Path $destDir) { Remove-Item -Recurse -Force $destDir }
Copy-Item -Recurse -Path $builtDir -Destination $destDir -Force

$destExe = Join-Path $destDir 'yt-dlp.exe'
if (-not (Test-Path $destExe)) { throw "Copy failed: $destExe missing" }

# Remove any stale single-file build so a dirty tree cannot ship both layouts
# (and so the app's legacy fallback does not silently mask a broken onedir).
$staleExe = Join-Path $binDir 'yt-dlp.exe'
if (Test-Path $staleExe) { Remove-Item -Force $staleExe }

$pkgZip = Join-Path $binDir 'yt-dlp-pkg.zip'
if (Test-Path $pkgZip) { Remove-Item -Force $pkgZip }
# Zip the CONTENTS of the directory, not the directory itself: the app extracts
# straight into bin/yt-dlp/ and expects yt-dlp.exe at the archive root.
Compress-Archive -Path (Join-Path $destDir '*') -DestinationPath $pkgZip -CompressionLevel Optimal

$builtSha = (Get-FileHash -Algorithm SHA256 -Path $destExe).Hash.ToLowerInvariant()
$pkgSha   = (Get-FileHash -Algorithm SHA256 -Path $pkgZip).Hash.ToLowerInvariant()
$fileCount = (Get-ChildItem -Recurse -File $destDir).Count
Write-Host ""
Write-Host "Built patched yt-dlp (onedir)"
Write-Host "  Directory      : $destDir ($fileCount files)"
Write-Host "  Exe SHA256     : $builtSha"
Write-Host "  Package        : $pkgZip"
Write-Host "  Package SHA256 : $pkgSha"
Write-Host "  Package size   : $((Get-Item $pkgZip).Length) bytes"
Write-Host "  Upstream commit: $upstreamSha"
Write-Host ""

# ─── 8. Smoke-test the built exe ─────────────────────────────────────────
Write-Host "Smoke test: yt-dlp.exe --version"
$versionOutput = & $destExe --version 2>&1
Write-Host "  $versionOutput"
if ($LASTEXITCODE -ne 0) {
    throw "Built yt-dlp.exe failed smoke test (--version returned $LASTEXITCODE)"
}
Write-Host "Smoke test passed."

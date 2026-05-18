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
    # yt-dlp's entry module is yt_dlp/__main__.py. --onefile produces a single
    # bundled exe. --runtime-hook injects our hook so it runs before yt-dlp
    # imports ssl.
    & python -m PyInstaller `
        --runtime-hook ytdlp_seclevel_hook.py `
        --name yt-dlp `
        --onefile `
        --console `
        --collect-all yt_dlp `
        --noconfirm `
        yt_dlp/__main__.py 2>&1 | Tee-Object -Variable pyinstallerOutput | Out-Null

    $builtExe = Join-Path $ytDlpRoot 'dist/yt-dlp.exe'
    if (-not (Test-Path $builtExe)) {
        Write-Host "PyInstaller output:"
        $pyinstallerOutput | ForEach-Object { Write-Host "  $_" }
        throw "PyInstaller did not produce dist/yt-dlp.exe"
    }
} finally {
    Pop-Location
}

# ─── 7. Copy to OutputDir/bin and report ─────────────────────────────────
$binDir = Join-Path $OutputDir 'bin'
New-Item -ItemType Directory -Force -Path $binDir | Out-Null
$destExe = Join-Path $binDir 'yt-dlp.exe'
Copy-Item (Join-Path $ytDlpRoot 'dist/yt-dlp.exe') $destExe -Force

$builtSha = (Get-FileHash -Algorithm SHA256 -Path $destExe).Hash.ToLowerInvariant()
Write-Host ""
Write-Host "Built patched yt-dlp.exe"
Write-Host "  Path           : $destExe"
Write-Host "  SHA256         : $builtSha"
Write-Host "  Size           : $((Get-Item $destExe).Length) bytes"
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

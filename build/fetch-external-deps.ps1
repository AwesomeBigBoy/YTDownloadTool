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

# yt-dlp: v1.5.0+ ships upstream's OFFICIAL Windows onedir build, byte-for-byte.
#
# v1.3.0-v1.4.0 compiled a patched yt-dlp.exe from source instead. An unsigned
# PyInstaller binary with a hash nobody else has is exactly what endpoint
# security distrusts: an endpoint-protection agent's behaviour monitoring held
# every launch ~27 s and then refused it ("Access denied"), while the official
# build ran in under a second on the same machine. The one customisation the patch carried now ships as a
# yt-dlp plugin inside YtDlpTool.exe (src/YtDlpTool.Process/YtDlpTlsPlugin.cs).
#
# Two outputs, both from the same verified zip:
#   bin/yt-dlp/          expanded, for the portable folder a fresh install gets
#   bin/yt-dlp-pkg.zip   the zip itself, which the in-app updater delivers to
#                        existing installs; YtDlpLayout expands it on next launch.
#                        Upstream's zip already has yt-dlp.exe + _internal/ at its
#                        root, which is the layout YtDlpLayout expects.
$ytZip = Join-Path $env:TEMP "yt-dlp-win-$(New-Guid).zip"
Write-Host "Downloading yt-dlp from $($manifest.'yt-dlp'.url)"
Invoke-WebRequest -Uri $manifest.'yt-dlp'.url -OutFile $ytZip -UseBasicParsing
$ytdlpSha = Verify-Sha256 -Path $ytZip -Expected $manifest.'yt-dlp'.sha256

$ytDlpDir = Join-Path $binDir 'yt-dlp'
if (Test-Path $ytDlpDir) { Remove-Item -Recurse -Force $ytDlpDir }
# ZipFile rather than Expand-Archive: same result, and it copes with long paths
# under Windows PowerShell 5.1 where Expand-Archive does not.
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::ExtractToDirectory($ytZip, $ytDlpDir)
$ytDlpExe = Join-Path $ytDlpDir 'yt-dlp.exe'
if (-not (Test-Path $ytDlpExe)) { throw "yt-dlp_win.zip did not contain yt-dlp.exe at its root" }

# A stale single-file build from <= v1.3.8 must not ship alongside, or the app's
# legacy fallback could silently mask a broken onedir install.
$staleExe = Join-Path $binDir 'yt-dlp.exe'
if (Test-Path $staleExe) { Remove-Item -Force $staleExe }

Copy-Item -Path $ytZip -Destination (Join-Path $binDir 'yt-dlp-pkg.zip') -Force
Remove-Item -Force $ytZip

# UpdateChecker compares the manifest version with what `yt-dlp --version` prints
# on the user's machine; a mismatch means an "update available" prompt that never
# goes away. Fail the build instead.
$reported = (& $ytDlpExe --version 2>&1 | Select-Object -First 1 | Out-String).Trim()
if ($reported -ne $manifest.'yt-dlp'.version) {
    throw "yt-dlp --version reported '$reported' but external-deps.json pins '$($manifest.'yt-dlp'.version)'"
}
Write-Host "yt-dlp $reported ready (official onedir build)"

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

# v1.1.32: also extract ffprobe.exe — yt-dlp needs it for --embed-thumbnail
# in the VideoOnly path. Without ffprobe, single-stream downloads fail at
# ~70% with the misleading "ffprobe not found" / ComponentMissing error.
$ffprobeExeSrc = Join-Path $ffmpegExtractDir $manifest.ffmpeg.ffprobeInsideZip
if (-not (Test-Path $ffprobeExeSrc)) {
    throw "ffprobe.exe not found at expected path inside zip: $($manifest.ffmpeg.ffprobeInsideZip)"
}
Copy-Item -Path $ffprobeExeSrc -Destination (Join-Path $binDir 'ffprobe.exe') -Force

Remove-Item -Recurse -Force $ffmpegExtractDir
Remove-Item -Force $ffmpegZip

Write-Host "External deps prepared in $binDir"
Write-Host "  yt-dlp_win.zip SHA-256 = $ytdlpSha"
Write-Host "  ffmpeg.zip     SHA-256 = $ffmpegZipSha"

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

# yt-dlp: v1.3.0+ does NOT download the prebuilt yt-dlp.exe. It is built from
# upstream source by build/build-patched-ytdlp.ps1, which the release workflow
# runs before this script. We just compute the SHA256 of whatever ended up in
# bin/ for the manifest. If yt-dlp.exe is missing, warn but don't fail — that
# lets local-dev runs of this script (without the build step) still complete
# so devs can iterate on ffmpeg handling.
# v1.4.0: yt-dlp is now a --onedir build living in bin/yt-dlp/, not a single
# bin/yt-dlp.exe. Accept either so this script keeps working while a dev has an
# older tree around.
$ytDlpDirExe = Join-Path $binDir 'yt-dlp/yt-dlp.exe'
$ytDlpFlatExe = Join-Path $binDir 'yt-dlp.exe'
if (Test-Path $ytDlpDirExe) {
    $ytdlpSha = (Get-FileHash -Algorithm SHA256 -Path $ytDlpDirExe).Hash.ToLowerInvariant()
    Write-Host "yt-dlp layout: onedir (bin/yt-dlp/)"
} elseif (Test-Path $ytDlpFlatExe) {
    $ytdlpSha = (Get-FileHash -Algorithm SHA256 -Path $ytDlpFlatExe).Hash.ToLowerInvariant()
    Write-Host "yt-dlp layout: legacy onefile (bin/yt-dlp.exe)"
} else {
    Write-Warning "yt-dlp not present in $binDir. Run build/build-patched-ytdlp.ps1 before this script for a real release build."
    $ytdlpSha = '<not-built>'
}

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
Write-Host "  yt-dlp.exe  SHA-256 = $ytdlpSha"
Write-Host "  ffmpeg.zip  SHA-256 = $ffmpegZipSha"

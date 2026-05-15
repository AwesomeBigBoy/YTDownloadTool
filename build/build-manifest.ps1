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
    },
    @{
        # v1.1.32: ffprobe.exe ships alongside ffmpeg.exe (same version, same
        # component so the updater treats them as a unit). Required by yt-dlp's
        # --embed-thumbnail step for single-stream downloads (VideoOnly mode).
        name = 'ffprobe.exe'; component = 'Ffmpeg'; version = $FfmpegVersion
        path = Join-Path $PortableDir 'bin\ffprobe.exe'; rel = 'bin\ffprobe.exe'
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

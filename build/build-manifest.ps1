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
        # v1.4.0: yt-dlp ships as a --onedir build, delivered as a zip.
        #
        # This stays an ORDINARY manifest entry on purpose. UpdateApplier
        # downloads it, verifies its SHA-256 and Sigstore signature, and moves it
        # into place with backup-and-rollback exactly like any other file — it has
        # no idea the payload is a directory, and needed no changes. The app
        # expands bin\yt-dlp-pkg.zip into bin\yt-dlp\ on its next launch (see
        # src/YtDlpTool.Process/YtDlpLayout.cs). Teaching the updater to swap
        # directories would have meant changing the one component whose failure
        # cannot be repaired remotely.
        #
        # Clients older than v1.4.0 handle this safely too: to them it is just a
        # file they never execute, and their existing bin\yt-dlp.exe keeps working
        # until the app binary that understands the package is installed.
        #
        # `version` MUST equal what `yt-dlp --version` prints on the user's
        # machine. UpdateChecker compares the two, so any drift produces a
        # permanent "update available" prompt that never clears.
        name = 'yt-dlp-pkg.zip'; component = 'YtDlp'; version = $YtDlpVersion
        path = Join-Path $PortableDir 'bin\yt-dlp-pkg.zip'; rel = 'bin\yt-dlp-pkg.zip'
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

# YtDlpTool

A lightweight Windows desktop YouTube downloader built on yt-dlp. Fast dark UI, hardened security, portable folder distribution, one-click Sigstore-verified updates. Everything it needs ships in the folder — no Python, no .NET install, no administrator rights.

## Install

1. Download `YtDlpTool-vX.Y.Z-win-x64.zip` from [Releases](https://github.com/AwesomeBigBoy/YTDownloadTool/releases/latest).
2. Unzip anywhere. No installation required.
3. Run `YtDlpTool.exe`.

### SmartScreen warning (first run)

YtDlpTool v1 is not Authenticode-signed. The first time you launch it Windows SmartScreen may show "Windows protected your PC".

To allow it:

1. Click **More info**.
2. Click **Run anyway**.

Alternative if AppLocker blocks even after that: right-click `YtDlpTool.exe` -> **Properties** -> tick **Unblock** at the bottom -> **OK**.

#### 首次執行 SmartScreen 警告 (zh-TW)

YtDlpTool v1 沒有 Authenticode 簽章，首次啟動時 Windows SmartScreen 可能會顯示「Windows 已保護你的電腦」。

允許執行：

1. 按一下 **其他資訊**。
2. 按一下 **仍要執行**。

若 AppLocker 仍然封鎖：在 `YtDlpTool.exe` 上按右鍵 -> **內容** -> 在底部勾選 **解除封鎖** -> **確定**。

Future versions may be signed via Azure Trusted Signing (no SmartScreen warning).

## Verify the download (optional, recommended)

Each release includes Sigstore signatures. To verify locally with cosign:

```pwsh
cosign verify-blob `
  --bundle YtDlpTool.exe.sigstore `
  --certificate-identity-regexp "^https://github\.com/AwesomeBigBoy/YTDownloadTool/\.github/workflows/release\.yml@refs/tags/v.*$" `
  --certificate-oidc-issuer "https://token.actions.githubusercontent.com" `
  YtDlpTool.exe
```

The same command form works for `yt-dlp-pkg.zip`, `ffmpeg.exe`, `ffprobe.exe`, and `manifest.json` against their respective `.sigstore` bundles. `yt-dlp-pkg.zip` is yt-dlp's official `yt-dlp_win.zip`, unmodified — its SHA-256 matches the `SHA2-256SUMS` file of the yt-dlp release it came from.

## Features

- Single YouTube URL → choose format (video / audio / silent video) → choose any available resolution → download
- Playlists and several links at once: pick the videos from a list, one format and a quality ceiling for all of them
- Paste, Ctrl+V or drag links (or a browser's .url shortcut) onto the window; Shorts, mobile and music links work too
- Remembers the format and quality of the last download
- File names carry the quality (`Title [1080p].mp4`, `Title [1080p 無聲].mp4`, `Title [128kbps].m4a`), so different qualities of one video sit side by side
- Retry for failed downloads; download progress on the taskbar button
- Re-adding a partly downloaded playlist: answer "file already exists" once for the rest of it; links not yet added when the app closes are offered again next time
- Up to 10 concurrent downloads (default 2)
- Subtitle download + embed (up to 3 languages per video)
- Time-range clipping (precise, keyframe-aligned)
- Cover art (the video thumbnail) embedded in audio downloads
- One-click background updates with Sigstore verification

## Security

- All YouTube URLs validated against a strict allowlist before invoking yt-dlp.
- Subprocess invocation uses argument arrays (no shell), a pinned PATH with Python hijack variables removed, and stdout buffer caps.
- Updates verify both a Sigstore-signed manifest and a Sigstore signature on every individual file.
- App stores nothing remotely; no telemetry. Logs do not include URLs or video titles.

## License

MIT for YtDlpTool. Bundled binaries retain their upstream licenses:
- yt-dlp: Unlicense
- ffmpeg: GPL/LGPL (this build is the `essentials` GPL build from gyan.dev)
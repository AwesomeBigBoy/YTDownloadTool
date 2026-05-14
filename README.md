# YtDlpTool

A lightweight Windows desktop YouTube downloader built on yt-dlp. Aurora Glass UI, hardened security, portable folder distribution, one-click Sigstore-verified updates.

> **Note:** The URLs in this README use `placeholder-owner/YtDlpTool` as a stand-in. This repo does not have a real GitHub remote yet. Before the first public release, search-and-replace `placeholder-owner/YtDlpTool` throughout this README, `src/YtDlpTool/AppHost.cs`, and `src/YtDlpTool/Dialogs/SettingsDialog.xaml.cs` with the real `<owner>/<repo>`.

## Install

1. Download `YtDlpTool-vX.Y.Z-win-x64.zip` from [Releases](https://github.com/placeholder-owner/YtDlpTool/releases/latest).
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
  --certificate-identity-regexp "^https://github\.com/placeholder-owner/YtDlpTool/\.github/workflows/release\.yml@refs/tags/v.*$" `
  --certificate-oidc-issuer "https://token.actions.githubusercontent.com" `
  YtDlpTool.exe
```

The same command form works for `yt-dlp.exe`, `ffmpeg.exe`, and `manifest.json` against their respective `.sigstore` bundles.

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

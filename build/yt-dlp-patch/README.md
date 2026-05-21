# yt-dlp ssl-compat patch

A small PyInstaller runtime hook (`ytdlp_seclevel_hook.py`) that is bundled
into our build of `yt-dlp.exe`. It lowers OpenSSL's per-context TLS security
level only when the environment variable `YTDLP_RELAX_SECLEVEL=1` is set.

When the env var is unset, the hook is a no-op and `yt-dlp.exe` behaves
identically to upstream's prebuilt binary.

## Why this exists

Some networks use HTTPS inspection products whose dynamically-generated
leaf certificates use legacy key sizes that OpenSSL's default security
level rejects at handshake time, before any Python-level certificate
validation runs. Users on those networks need a way to opt out of the
strict default while keeping certificate chain and hostname validation
intact.

This hook gives them that opt-out, scoped to a single env var and
gated behind an explicit user setting in the host application.

## Activation flow

```
Default user:
  YtDlpTool launches yt-dlp.exe with default environment
    ↓
  Runtime hook checks YTDLP_RELAX_SECLEVEL → unset → no-op
    ↓
  yt-dlp behaves as upstream (full SECLEVEL=1, no key relaxation)

Opt-in user (Settings → 進階 → 「允許不受信任憑證」 enabled):
  YtDlpTool launches yt-dlp.exe with YTDLP_RELAX_SECLEVEL=1 in env
    ↓
  Runtime hook checks env → matches → monkey-patches ssl.SSLContext.__init__
    ↓
  Every SSLContext created downstream gets set_ciphers('DEFAULT@SECLEVEL=0')
    ↓
  TLS handshake succeeds with legacy-key-size certs
    ↓
  Certificate chain + hostname validation continue normally
```

## What the patch does NOT do

- Does not disable certificate chain validation
- Does not disable hostname checking
- Does not accept any certificate from anyone
- Does not send any telemetry, make any network calls, or read/write files
  outside what upstream yt-dlp itself does
- Does not modify yt-dlp's source code — it is added as a PyInstaller
  `--runtime-hook` that runs once at process start

## Build process (this is what `release.yml` runs in CI)

1. `build/external-deps.json` pins the exact upstream commit SHA + the SHA256
   of the downloaded source archive
2. The build script (`build/build-patched-ytdlp.ps1`) downloads yt-dlp source
   at that commit, verifies the SHA256, installs PyInstaller, and runs
   `PyInstaller --runtime-hook=ytdlp_seclevel_hook.py` on yt-dlp's entry point
3. The resulting `yt-dlp.exe` is signed with Sigstore via the same release
   workflow that signs every other artifact

The build is fully scripted and runs in GitHub Actions, so the Sigstore
transparency log entry records exactly which commit, workflow run, and
environment produced the binary.

## Verifying

Future v1.3.0 release adds an in-app verification dialog (Settings → 關於 →
「驗證 yt-dlp.exe 來源」) and a standalone PowerShell script in the release
archive that reproduces the verification.

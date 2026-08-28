# Architecture

## What this tool is

A Windows desktop application that wraps `yt-dlp.exe` + `ffmpeg.exe` so an
end-user can paste a YouTube URL, pick a format, and save a file. Distributed
as a single portable folder — no installer, no admin rights, no per-machine
state. Self-updates against signed GitHub Releases. Built for managed
managed environments where Chocolatey / installers are blocked.

## Process model

Single GUI process (`YtDlpTool.exe`) that spawns short-lived `yt-dlp.exe`
and `ffmpeg.exe` children for each job. All persistent state lives in the
portable folder itself. No background service, no scheduled task, no
elevated helper.

```
+---------------------------------------------------------------+
|  YtDlpTool.exe  (WPF, .NET 8, single-file self-contained)     |
|                                                               |
|  +------------------+    +--------------------------------+   |
|  |  WPF UI layer    |    |  AppHost (composition root)    |   |
|  |  Views, VMs      |<-->|  Config / Logger / Paths       |   |
|  +------------------+    |  YtDlpRunner / FfmpegRunner    |   |
|                          |  DownloadQueue                 |   |
|                          |  UpdateChecker / UpdateApplier |   |
|                          +-------+------------------------+   |
|                                  |                            |
|                                  | ProcessSandbox.RunAsync    |
|                                  v                            |
|                          +---------------+                    |
|                          | child process |   yt-dlp.exe       |
|                          | (per-job)     |   ffmpeg.exe       |
|                          +---------------+                    |
+---------------------------------------------------------------+
        |                                ^
        | downloads + sigstore verify    | network: HTTPS to
        v                                | github.com /
        +--- GitHub Releases ------------+  fulcio / rekor
```

## Project layout

The solution lives at `YtDlpTool.sln`. Five projects.

| Project | Role | Depends on |
|---|---|---|
| `src/YtDlpTool` | WPF UI, App entry, AppHost composition | Process, Domain |
| `src/YtDlpTool.ViewModels` | MVVM view models, MVVM Toolkit source-generated | Domain |
| `src/YtDlpTool.Process` | Subprocess execution; ProcessSandbox, YtDlpRunner, FfmpegRunner | Domain |
| `src/YtDlpTool.Domain` | Pure types: Config, Logging, Persistence, Updates, Security, Services. **No I/O except where unavoidable.** | (none) |
| `tests/YtDlpTool.Domain.Tests` | xUnit, 156 tests | Domain |
| `tests/YtDlpTool.Process.Tests` | xUnit, 41 tests (+ FakeYtDlp helper exe) | Process |
| `tests/FakeYtDlp` | Tiny console app the Process tests invoke as a stand-in for yt-dlp | (none) |

## Key types and what they do

### Composition

- **`AppHost`** (`src/YtDlpTool/AppHost.cs`): single composition root. Built
  once in `App.OnStartup`. Holds all singletons: Logger, ConfigStore,
  YtDlpRunner, UpdateChecker, DownloadQueue, etc. No DI container — manual
  wiring on purpose (keeps single-file publish lean, no reflection cost).
- **`AppPaths`** (`src/YtDlpTool.Domain/AppPaths.cs`): resolves where files
  live. **Everything is relative to the .exe** so the portable folder is
  self-contained. `DataRoot` = the .exe's directory.

### Subprocess execution

- **`ProcessSandbox`** (`src/YtDlpTool.Process/ProcessSandbox.cs`): the only
  way the app launches a child process. Enforces:
  - PATH override (binDir + System32 — defense against PATH hijack)
  - PYTHON encoding hints (deterministic UTF-8 output)
  - stdin redirect + immediate close (avoids isatty hangs in GUI parent)
  - Stdout/stderr capture with byte caps
  - Last-30-lines stdout ring buffer for diagnostics on hang
  - Timeout + cancellation
- **`YtDlpRunner`** (`src/YtDlpTool.Process/YtDlpRunner.cs`): builds yt-dlp
  argument lists, calls ProcessSandbox. Three public methods:
  - `FetchMetadataAsync` — `--dump-single-json`, 30s timeout
  - `DownloadAsync` — full file, streaming progress parse, no built-in timeout
  - `DownloadSubtitlesOnlyAsync` — `--skip-download --write-subs`, 2 min timeout
- **`FfmpegRunner`** (`src/YtDlpTool.Process/FfmpegRunner.cs`): clip cutting
  via stream-copy (`-c copy`) and audio remux.

### Update pipeline

- **`UpdateChecker`** (`src/YtDlpTool.Domain/Updates/UpdateChecker.cs`):
  polls the latest GitHub Release manifest, verifies its Sigstore signature,
  compares versions against installed.
- **`UpdateApplier`** (`src/YtDlpTool.Domain/Updates/UpdateApplier.cs`):
  downloads each file, verifies each file's Sigstore signature individually,
  atomic rename into place, restart prompt.
- **`SigstoreVerifier`** (`src/YtDlpTool.Domain/Security/SigstoreVerifier.cs`):
  verifies cosign legacy bundles (`base64Signature/cert/rekorBundle`).
  Handles Fulcio leaf → intermediate → root chain. Re-checks transparency
  log inclusion in Rekor.

### Queue + execution

- **`DownloadQueue`** (`src/YtDlpTool.Domain/Services/DownloadQueue.cs`):
  per-`Config.ConcurrentDownloads` worker model (default 2). Each job goes
  through Pending → Running → (Done | Failed | Cancelled).
- **`YtDlpDownloadExecutor`** (`src/YtDlpTool.Domain/Services/YtDlpDownloadExecutor.cs`):
  orchestrates the actual work for one job: yt-dlp metadata → yt-dlp download
  → ffmpeg clip (if requested) → ffmpeg subtitle mux (if subs requested).
- **`StateJournal`** (`src/YtDlpTool.Domain/Persistence/StateJournal.cs`):
  append-only `state.log` so a crash mid-download lets us tell the user
  what was in-flight on next launch. **Exclusive file lock** — only one
  instance of the app per portable folder.

### Configuration + logging

- **`AppConfig`** (`src/YtDlpTool.Domain/Models/AppConfig.cs`): plain
  serializable config. Stored as `config.json`. Source-generated JSON
  via `System.Text.Json` to stay AOT-friendly.
- **`AppLogger`** (`src/YtDlpTool.Domain/Logging/AppLogger.cs`): structured
  key/value log lines, daily files in `logs/`, auto-purges after 7 days.
  **Never logs raw URLs or titles** — always `HashSuffix(url)` for privacy.
  This rule is non-negotiable: every URL touches user data and could end up
  in a bug report screenshot.

## Data flow: one download

```
User pastes URL
        |
        v
URL Validator (UrlValidation.cs) — strict allowlist; reject anything weird
        |
        v
MetadataFetchViewModel.ParseUrlAsync
        |
        v
YtDlpRunner.FetchMetadataAsync  (--dump-single-json, 30s timeout)
        |
        v
ProcessSandbox.RunAsync — spawn yt-dlp.exe with sandboxed PATH + injected
                          SSL_CERT_FILE, capture stdout JSON
        |
        v
VideoMetadata + Format list shown in UI
        |
        v
User picks format / mode (audio|video|both) / clip range / subs / save dir
        |
        v
DownloadQueue.Enqueue → worker picks up
        |
        v
YtDlpDownloadExecutor.RunAsync
   |
   +--> YtDlpRunner.DownloadAsync (media, may include --embed-thumbnail)
   |        produces progress lines parsed by YtDlpRunner.ParseProgress
   |
   +--> [if subs requested]
   |        YtDlpRunner.DownloadSubtitlesOnlyAsync (--skip-download)
   |
   +--> [if clip requested]
   |        FfmpegRunner.ClipAsync (-c copy, stream-copy)
   |
   +--> [if subs + non-clip]
   |        FfmpegRunner.MuxSubsAsync (mux sub tracks into MP4)
   |
   v
Final file in user's save directory
StateJournal records "completed"
UI marks job done
```

## Network endpoints (what the app talks to)

- `https://api.github.com` — release manifest, releases list
- `https://github.com` — release asset downloads
- `https://objects.githubusercontent.com` — release asset CDN
- `https://fulcio.sigstore.dev` — Sigstore certificate authority (cert chain)
- `https://rekor.sigstore.dev` — Sigstore transparency log

For YouTube itself, **the app never talks to YouTube directly**. yt-dlp does
all of that. We do not parse video URLs, we do not call YouTube APIs, we
do not handle PO Tokens. yt-dlp is the entire YouTube-facing surface.

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
| `src/YtDlpTool` | WPF UI (Views, ViewModels — MVVM Toolkit source-generated — Dialogs, the dark `Resources/Theme.xaml`), App entry, AppHost composition | Process, Domain |
| `src/YtDlpTool.Process` | Subprocess execution; ProcessSandbox, YtDlpRunner, FfmpegRunner | Domain |
| `src/YtDlpTool.Domain` | Pure types: Config, Logging, Persistence, Updates, Security, Services. **No I/O except where unavoidable.** | (none) |
| `tests/YtDlpTool.Domain.Tests` | xUnit, 209 tests | Domain |
| `tests/YtDlpTool.Process.Tests` | xUnit, 80 tests (+ FakeYtDlp helper exe) | Process |
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
- **`YtDlpLayout`** (`src/YtDlpTool.Process/YtDlpLayout.cs`): yt-dlp lives in
  `bin\yt-dlp\` (onedir). An update delivers `bin\yt-dlp-pkg.zip` — upstream's
  official `yt-dlp_win.zip`, unmodified. Since v1.5.0 every update path puts it
  into service right after the download (`ActivatePackage`, via
  `AppHost.ActivateYtDlpPackageAsync`); one left over (an update that also replaced
  YtDlpTool.exe, or a swap that failed) is expanded in the background on the next
  launch, after the window is up. Directory renames retry for up to 10 s because
  antivirus holds freshly written files open.
- **Links in** (v1.5.0): 貼上, Ctrl+V (window and URL box) and drag-and-drop all go
  through `MainWindow.HandleIncomingText` → `LinkExtractor` (Domain; every candidate
  passes `UrlValidator`, which also accepts /shorts/, /live/, /embed/, m. and music.).
  One video → the URL box as before; a playlist → `OpenPlaylistAsync`
  (`YtDlpRunner.FetchPlaylistAsync`, `--flat-playlist`); several links →
  `BatchDialog` → `MainViewModel.AddBatch`, which parses and queues them one by one.
- **Remembered choice** (v1.5.0): `AppConfig.LastMode/LastVideoQuality/LastAudioQuality`,
  written on 下載 and on batch confirm, resolved per video by
  `QualityOptions.PreferredIndex` (never above the remembered resolution).
- **Taskbar progress** (v1.5.0): `TaskbarProgressTracker` averages the current batch
  (finished members count as done, so the bar never runs backwards);
  `MainWindow.ApplyTaskbarProgress` drives `TaskbarItemInfo`.
- **`YtDlpTlsPlugin`** (`src/YtDlpTool.Process/YtDlpTlsPlugin.cs`): the only
  customisation of yt-dlp, as a yt-dlp plugin (`Plugins/ytdlptool_tls_relax.py`,
  embedded in the assembly) loaded with `--plugin-dirs` when the user enabled
  AllowUntrustedCertificates. v1.5.0 replaced the self-built patched yt-dlp.exe
  with it; see design commitment #13.

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
  instance of the app per portable folder. v1.5.0: also `LinkPending` /
  `LinkSettled` from **`BatchJournal`** — playlist / multi-link links not
  queued yet come back too.
- **Progress path (v1.5.0):** yt-dlp's stdout reader thread → `YtDlpRunner`
  (`ProgressReport`, `Processing` once post-processors run) → executor →
  `DownloadQueue` → `MainViewModel.OnQueueEvent`, all through
  **`InlineProgress<T>`** (synchronous, in order). The UI throttle
  (`ProgressThrottle`) runs on that thread; only what passes is posted to
  the dispatcher. `Progress<T>` used to post every line to the UI thread first.
- **`ConflictDecisions`** (Domain): one 檔案已存在 dialog at a time; an answer
  given "for the rest of this batch" (`DownloadJob.BatchId`) answers the others.
- **`OutputNames`** (Domain): the folder + stem of every running download; a second one
  with the same name takes `<title>_2`. `CoverArt` decides `--embed-thumbnail` (音訊 only).
- **Final name (v1.5.0):** with no clip, no subtitles and not 音訊, yt-dlp writes
  `<title>.<ext>` itself; otherwise it writes `<title>.media.<ext>` and the executor
  cuts / muxes / renames. **`DownloadLeftovers`** tells yt-dlp's work files for a
  name (`.fNNN.<ext>`, `.part`, `.ytdl`, `.temp.<ext>`) from finished files; they are
  cleared at the start of a job (no `--force-overwrites`, so `--continue` resumes
  within the job) and on failure.

### UI (v1.5.0)

- One window: native title bar darkened by `Interop/DarkTitleBar`, a composer card
  (`UrlInputView` → `VideoCardView` → format / quality / 下載 → 更多選項 + 儲存到)
  and `QueuePanelView`, which owns its own scrolling so it virtualizes.
- Decisions about what the UI shows live in Domain so they are unit-tested:
  `QualityOptions` (every resolution once, H.264 → VP9 → AV1, video-only streams
  first, Shorts by their shorter side, 1080p preselected), `QueueStatusText`
  (row status wording) and `ProgressThrottle` (≤ 4 progress updates/s per job).
- `Dialogs/AppDialog` replaces `MessageBox.Show` everywhere except the two crash
  reports in `App.xaml.cs`, which must work even if the theme failed to load.
- Rendering rules are design commitment #9.

### Configuration + logging

- **`AppConfig`** (`src/YtDlpTool.Domain/Models/AppConfig.cs`): plain
  serializable config. Stored as `config.json`. Source-generated JSON
  via `System.Text.Json` to stay AOT-friendly. v1.5.0: `ConfigStore` writes
  `config.json.bak` and then `config.json` in place (never a rename over it —
  endpoint security holds that ~3–6 s) and reads the `.bak` if the main file
  is missing or cut short.
- **`AppLogger`** (`src/YtDlpTool.Domain/Logging/AppLogger.cs`): structured
  key/value log lines, daily files in `logs/`, auto-purges after 7 days.
  v1.5.0: every line is flushed as it is written, so a killed process keeps
  its log.
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

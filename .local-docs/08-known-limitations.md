# Known limitations and deferred work

Things we know are not great. Some are deferred to a future major version;
some are genuinely "won't fix" by design; some are open questions.

## v1.3.0 SSL bypass — explicit trade-off (not a bug)

### What it is

`AllowUntrustedCertificates = true` disables HTTPS verification in yt-dlp
entirely (no issuer check, no key-length check, no hostname check).
Necessary to operate on networks whose inspection proxy presents short-key
leaf certs, which yt-dlp's pinned `@SECLEVEL=2` rejects ("EE certificate key
too weak", E-SSL02).

v1.5.0 mechanics (yt-dlp is upstream's official build now, so nothing can be
compiled into it): every yt-dlp invocation — metadata, download **and**
subtitles — gets `--no-check-certificates` plus `--plugin-dirs` pointing at
`YtDlpTlsPlugin`'s directory, and the env var `YTDLP_RELAX_SECLEVEL=1` that the
plugin checks before doing anything. Measured against a local server with a
legacy-key-size RSA leaf: plain → E-SSL02; flag alone → works; plugin alone → works.
Before v1.5.0 the download path lacked the flag and depended entirely on the
patched exe. The plugin is written to disk only when the setting is on.

### Why it's a trade-off, not "bad code"

The CA-bundle-based approach alone (v1.1.17 era) handles the trust
chain problem cleanly but cannot bypass OpenSSL's handshake-time key-
strength rejection. There is no Python-ssl-level setting that lowers
SECLEVEL without also requiring CERT_NONE (Python's `set_ciphers` with
`@SECLEVEL=0` only affects cipher selection, not the handshake
acceptance gate). The choices are:

| Option | Pros | Cons |
|---|---|---|
| Don't ship the bypass | Maximally safe | Tool is unusable on the affected networks |
| Bypass only at handshake (theoretical) | Keeps chain validation | No Python API exposes this — would need OpenSSL fork |
| **v1.3.0 approach: full bypass when user opts in** | Works; explicit consent UX; audit log; default-off | Users on hostile networks who flip the flag get full MITM exposure |

### Mitigations actually shipped

- Default `false` — most users never see this
- UI copy explicitly enumerates the three checks being disabled
- Confirmation modal repeats the warning before save
- Auto-prompt only fires after a real SSL failure (so user has
  reason to consider the trade-off)
- `config.changed` log event records every flip with timestamp +
  trigger source — forensic trail
- Sigstore update verification is independent of SSL — even with the
  flag flipped, malicious update assets are still rejected at signature
  verification

### Open question

Future Python releases may expose a finer-grained API (e.g. cipher-
level SECLEVEL override without verify_mode change). If that lands,
v1.4+ could narrow the bypass to "accept short keys, still validate
chain + hostname". For now the full bypass is the only working option.

## Deferred to v1.2 (or beyond)

### Authenticode signing
- **Why deferred:** costs $$$/year for a Standard or EV cert; key
  management is non-trivial; v1 scope was minimum viable.
- **Status:** v1 ships unsigned; first-launch SmartScreen warning is
  documented in public README in both English and Traditional Chinese.
- **Path forward:** Azure Trusted Signing (cheap, cloud-managed) when
  user volume justifies it. Updates required in:
  - `release.yml` — add a signing step after `dotnet publish`
  - public README — remove SmartScreen documentation
  - `04-update-flow.md` — note Authenticode adds a SECOND verification
    layer alongside Sigstore (both should pass)

### Multi-instance support
- **Why deferred:** StateJournal opens `state.log` with exclusive lock —
  by design, only one app instance per portable folder.
- **Status:** working as intended. Second instance shows friendly error
  on startup.
- **Path forward:** if needed, redesign StateJournal as SQLite WAL-mode
  (or similar concurrent-writer journal) before relaxing the lock.

### Proxy authentication
- **Why deferred:** the Negotiate / NTLM proxy auth path is
  thorny. yt-dlp supports `http://user:pass@host:port` style basic auth
  via `--proxy`, but most managed proxies use Negotiate which yt-dlp's
  Python stack doesn't natively handle.
- **Status:** if `HTTP_PROXY` env var has credentials, they pass through.
  If proxy requires Negotiate, user is out of luck.
- **Path forward:** consider shipping a small native proxy shim that
  handles Negotiate auth on Windows and forwards to upstream. Significant
  scope; punt until multiple users hit it.

### Cookies / sign-in
- **Why deferred:** out of scope for v1. Many videos work fine anonymously.
- **Status:** no cookie support. Members-only videos fail.
- **Path forward:** yt-dlp supports `--cookies-from-browser <browser>`.
  Could expose this in Settings → 進階. Privacy implications: cookies
  contain auth tokens; if we read them, they're in our process memory,
  could end up in crash dumps. Design carefully.

### Re-encoding controls
- **Why:** clip is `-c copy` (stream copy, lossless), audio re-encode
  to mp3 happens only when user picks audio-only mode and the source
  is not already m4a/aac.
- **Status:** no UI for picking codec or bitrate.
- **Path forward:** advanced settings panel if users ask. Most don't.

## Decided in v1.5.0

### Playlists and several links: each video is parsed on its own
- **What:** a playlist link (or a video link carrying `list=`) opens 加入多部影片:
  `YtDlpRunner.FetchPlaylistAsync` reads the list with `--flat-playlist` (one call,
  at most `MaxPlaylistEntries` = 500 videos), the user ticks videos and picks one
  格式 and a 畫質 ceiling, and `MainViewModel.AddBatch` then parses and queues each
  video one at a time exactly like a pasted link. A paste or drop with several links
  opens the same list. Single-video calls keep `--no-playlist`.
- **Why one at a time:** every video gets the same format choice as a pasted link
  (H.264 first, the remembered quality or the nearest lower one), and the parses do
  not compete with downloads already running. A 50-video playlist takes a few
  minutes to queue; rows appear as they are added and downloads start immediately.
- **Mix lists (RD…):** YouTube refuses them on /playlist ("This playlist type is
  unviewable"), so the card's 「選擇清單裡的影片…」 reads every list through the watch page
  (`PlaylistLinks.ForVideo`, watch?v=…&list=…), which yt-dlp expands. Lists that need
  sign-in (WL, LL, LM) get no link. A Mix yt-dlp cannot expand comes back as one video
  and is reported as such.
- **Protection in a batch:** duplicates and links already downloading are skipped (two
  jobs for one video would write the same files); three failures in a row that hit every
  video alike stop the rest; the batch's downloads end with one summary toast.
- **Not included:** subtitles and 擷取片段 for batches (the available languages and
  lengths differ per video), channel pages, and more than 500 videos per list.
- **Re-adding a partly downloaded playlist:** 檔案已存在 is asked one dialog at a time
  (`ConflictDecisions`), and 「這批其他已存在的檔案也這樣處理」 answers the rest of that
  batch (略過 / 覆蓋 / 自動改名). A 重試 of a batch download is asked again.
- **Closing mid-batch:** links not queued yet are journaled (`BatchJournal`,
  `LinkPending` / `LinkSettled` in state.log) and offered again at the next start with
  the interrupted downloads. Links dropped by 停止 or by the 3-in-a-row rule are not.

### yt-dlp no longer reads the user's own config or plugins
- **What:** every call passes `--ignore-config --no-plugin-dirs`
  (`YtDlpRunner.BuildCommonCliArgs`); the TLS plugin folder is added after the
  clear. Before this, a `%APPDATA%\yt-dlp\config.txt` (the development machine has
  one) or a portable `yt-dlp.conf` was applied to every app call — a stray
  `--download-archive` would have skipped downloads silently.
- **Cost:** someone who relied on such a file to make the app work (say a proxy
  line) loses that; the app's own proxy detection is the supported way.

### Dark theme only
- **What:** the light theme and the 主題 setting were removed with the Aurora UI.
  `AppConfig.Theme` / `ThemePreference` remain so older config.json files load;
  the value is ignored.
- **Why:** the redesign was asked for as a dark UI, and one palette keeps every
  resource a frozen StaticResource (design commitment #9).
- **If a light theme is wanted again:** add a second palette dictionary and move
  theme-dependent references back to DynamicResource; see commitment #9.

### File operations on PCs with endpoint security (measured 2026-11-04)
- **What:** on the development PC, endpoint security holds a rename over an existing
  file ~3 s (`File.Replace`: two renames, ~6 s), and holds operations by unsigned
  processes on .mp4 files ~3 s each; writing into a file or renaming to a new name takes
  ~2 ms. Nothing in yt-dlp's or the app's own logs shows it — only wall time does.
- **What the app does about it:** config.json is written in place with a `.bak` written
  first (it used File.Replace — 6 s on the UI thread at every 下載); the TLS plugin is
  written in place; a download with nothing to do after yt-dlp (no clip, no subtitles,
  not 音訊, final file absent) is written under its final name instead of
  `<title>.media.<ext>` plus a rename (−6 s per .mp4).
- **Cover art only for 音訊 (the user's call, 2026-11-04):** `--embed-thumbnail` was
  ~15–19 s of a ~37 s 影片 download of a 19-s clip (mutagen edits the .mp4 in place, then
  utime/delete, each held). `CoverArt.Embeds` now says yes for 音訊 only; a 19-s clip in
  影片 mode takes ~18 s (measured in the app: 38.5 s → 18.3 s). What remains is the merge
  and the deletes of the two streams (~3 s each); the row says 「80% · 處理中…」 meanwhile.
- **Cancelling takes a few seconds there:** yt-dlp is stopped at once, but each work file
  it leaves is deleted with a ~3 s hold; the row says 「正在取消…」 until it goes.
- An exclusion for the app folder and the save folder in the security product, or
  Authenticode signing, would remove the holds.
- **Not the cause:** `--throttled-rate` (it needs >3 s below 200 KiB/s on one request),
  `--concurrent-fragments`, the network (each stream arrives in <0.1 s at 4–11 MiB/s).

### Resume: --force-overwrites is no longer passed
- `--force-overwrites` implies `--no-continue`, so until v1.5.0 a 403 retry and every
  `--throttled-rate` re-extract restarted the stream at byte 0 (5 restarts in 45 s on a
  link capped at 100 KiB/s). The executor now clears yt-dlp's work files for the name at
  the start of a job (`DownloadLeftovers`: `.fNNN.<ext>`, `.part`, `.ytdl`, `.temp.<ext>`
  — never a finished file) and lets `--continue` resume within the job.
- **Two downloads of the same name:** `OutputNames` holds each running download's
  folder + stem; a second download of the same video (the composer keeps its options so
  people queue 1080p and then 720p) or of another video with the same title takes the next
  free name (`<title>_2`) instead of sharing every work file and the final name. Log:
  `download.name_in_use suffix=_2`.
- **A direct download that does not finish** (cancelled, failed, stopped by the watchdog)
  deletes its final-name file too: yt-dlp merges into it before its post-processors run.

### A resolution offered only as a combined (audio+video) stream
- `QualityOptions` prefers video-only streams, so this only happens when YouTube
  has nothing else at that height (rare; format 18 at 360p on old uploads).
  影片 mode then asks yt-dlp for `<id>+bestaudio`, which can leave a second audio
  track in the file. Fixing it means changing `YtDlpRunner.BuildFormatArgs`
  (download pipeline), which v1.5.0 deliberately did not touch.

## Won't fix (by design)

### JS runtime for yt-dlp extractor
- **Reason:** would inflate distribution by 50-100 MB (deno or node),
  and would tie us to a specific runtime version that yt-dlp recommends
  but doesn't strictly require.
- **Tradeoff accepted:** we lose some formats/features that need JS
  deobfuscation. yt-dlp prints a warning; we let users see it.
- **If this becomes a problem:** revisit, but probably as an optional
  "advanced" download in the in-app updater (not bundled by default).
- **Watch this one (2026-11):** yt-dlp 2026.10.26 prints "YouTube extraction
  without a JS runtime has been deprecated, and some formats may be missing"
  on every YouTube call. In practice all resolutions up to 2160p were still
  listed and downloadable on 2026-11-04. If users start seeing only low
  resolutions or empty format lists after a YouTube change that a yt-dlp bump
  does not fix, this decision has to be revisited. Note that ProcessSandbox
  rewrites PATH, so yt-dlp will not find a runtime the user installed
  themselves; it would have to be shipped and passed with `--js-runtimes`.
- **Data point (2026-11-04, same day):** 1 of 7 large downloads (1080p, 268 MB)
  failed with HTTP 403 at 7.8% — not reproducible on the next six. v1.5.0 re-runs a
  download once when a 403 arrives after data was flowing (`ytdlp.download.retry`).
  If that log line starts showing up often, it is the signal to ship a runtime.
  Facts for that decision, from yt-dlp's EJS wiki page: Deno ≥ 2.3.0 is the
  recommended runtime, enabled by default, and runs the solver without file or
  network access; Node.js ≥ 22 and QuickJS-NG ≥ 0.12 also work (older QuickJS
  builds can take minutes). The solver scripts (`yt_dlp_ejs`) are already inside
  the official `yt-dlp_win.zip`; only the runtime is missing. Deno is ~100 MB, so
  it should be its own manifest entry, not part of the yt-dlp package.

### Cross-platform (macOS, Linux)
- **Reason:** WPF is Windows-only. UI tier would need a complete rewrite
  in Avalonia or MAUI. Not worth it for the target user base.
- **If this becomes a problem:** that's a v2 product, not a port.

### Per-user vs per-machine install
- **Reason:** portable folder is intentionally per-user (well, per-folder).
  Multiple users on one machine each get their own copy with their own
  config.
- **Tradeoff accepted:** wastes disk on shared machines. Acceptable for
  a ~70MB tool.

### Telemetry / crash reporting
- **Reason:** privacy commitment, no Phone-home. Users send us logs
  manually when they want to.
- **Tradeoff accepted:** slower feedback loop on rare bugs.

## Open questions (revisit when you touch this)

### Should the CA bundle be regenerated on every launch?
Currently `SystemCertBundle.GenerateOrRefresh` overwrites the PEM on every
startup, ~50ms. Could cache and only refresh on a schedule (weekly?), but
the bundle changes when GPO pushes a new CA — caching would delay picking
that up. Current behavior: simple, slightly wasteful, always correct.

### Should `system-ca-bundle.pem` be cleaned up on exit?
It's currently left in `DataRoot` between runs. Not a leak (capped at
~300 KB), but a file the user might wonder about. Consider hiding it or
moving to `Paths.AppDataDirectory` (currently it's in the portable folder
root).

### Fulcio root rotation policy
Embedded Fulcio root is good until ~2030 by current Sigstore policy. If
they rotate sooner, every old client breaks until a new app build ships.
Acceptable risk for the v1 lifecycle. Future: consider a "trust-on-first-use
update" path where the client can fetch a fresh root from sigstore.dev,
verified by some out-of-band anchor. Significant complexity; defer.

### `Paths.DataRoot` for portable mode
Currently `Paths.DataRoot` resolves to the .exe's directory. That works
for portable folder mode. If we ever support an installer mode where
the app lives in `%ProgramFiles%`, `DataRoot` would need to be in user
profile instead. Be careful when refactoring `AppPaths` — multiple
callers assume DataRoot is writable.

### `state.log` schema
`StateJournal` writes structured records but the schema isn't versioned.
If we ever change the format, old logs become unparseable on first
post-upgrade launch. Should add a schema version line at file head,
ignore-and-discard on mismatch.

## Things we did but you might wonder why

### Why no FreeSpace check before download?
We don't precheck disk space. If the download fills the disk mid-flight,
yt-dlp errors out with a fairly clear message. Pre-check would require
guessing the final file size (we don't reliably know it from metadata
alone). Not worth the complexity for a rare case.

### Why so many comment paragraphs in code?
Look at `02-design-commitments.md` and `07-version-history.md`. Most
comments in this codebase explain a non-obvious decision tied to a
specific incident. They're load-bearing. **Don't strip them as "noise."**

### Why is `Logger.HashSuffix` only 8 hex chars?
8 hex = 32 bits = enough entropy for distinguishing URLs in a single
log session without being long enough to brute-force the URL by hashing
known YouTube IDs. Adequate for diagnostics, deliberately weak for
recovery. Don't lengthen.

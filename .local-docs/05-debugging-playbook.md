# Debugging playbook

For each observed failure mode, this lists: what to look at in the log,
what hypothesis it suggests, and what to do next. Pair this with
`07-version-history.md` — many entries here are also "the fix that's
already shipped, in case it regresses."

## How to read the log first

User logs live at `<portable-folder>/logs/YtDlpTool-YYYY-MM-DD.log`. Format:

```
HH:MM:SS.fff LEVEL category key1=value1 key2=value2 ...
```

The "early diagnostic" log lives at
`%TEMP%/YtDlpTool-startup-<timestamp>-<pid>.log` and captures events
**before** AppLogger is available (i.e., crashes during AppHost construction).
Always ask for both.

Key events to grep for:

| Pattern | Meaning |
|---|---|
| `startup.fatal` | App crashed during AppHost ctor (early diag only) |
| `unhandled.dispatcher` / `unhandled.domain` | Unexpected exception bubble |
| `ca-bundle.generated` / `ca-bundle.generation_failed` | SystemCertBundle did its thing |
| `ytdlp.invoke.begin` / `ytdlp.invoke.end` (v1.1.19+) | Each yt-dlp subprocess invocation, with structured timing |
| `url.parse.failed` | yt-dlp metadata fetch failed |
| `download.failed` | yt-dlp download failed |
| `update.check.error` / `update.manifest.verify_failed` | Update path |
| `system.proxy.detected` | One-time startup proxy fingerprint |
| `gpu.detected` (v1.2.1+) | Registry-read GPU adapter names |
| `render.mode reason=...` (v1.2.1+) | Why we chose hardware/software rendering |
| `config.path` / `config.loaded` (v1.2.1+) | Which config.json path was read + key flag values |
| `update.cleanup.stale` (v1.2.2+) | Startup deleted N stale `.old` files from the install folder |
| `update.cleanup.stale_failed` (v1.2.2+) | A `.old` file couldn't be deleted at startup — usually AV holding it; harmless |
| `ytdlp.package.activate result=… trigger=… elapsed_ms=…` (v1.5.0+) | A downloaded `yt-dlp-pkg.zip` was swapped in (`InPlace`) or left for the next launch (`NeedsRestart`, with `error=`). `trigger` = `startup` (background, after the window is up), `startup_legacy`, `repair` (health banner 修復), `banner_one_click`, `settings_check`, `settings_redownload`. Replaces v1.4.0's `ytdlp.package.expand` |
| `ytdlp.package.reprobe ok=… version=…` (v1.5.0+) | `--version` right after an in-place swap from an update path |
| `ytdlp.download.retry reason=forbidden_mid_stream` (v1.5.0+) | A download got HTTP 403 after data had arrived and was re-run once |
| `ytdlp.invoke.begin op=playlist` / `playlist.opened entries=N` (v1.5.0+) | A playlist was read with `--flat-playlist`; N videos listed (max 500) |
| `playlist.failed error_code=…` (v1.5.0+) | The playlist could not be read; the box showed 「無法讀取這個播放清單：…」 |
| `batch.item.failed error_code=…` (v1.5.0+) | One video of a playlist / multi-link batch could not be parsed (`no_formats`: parsed, but nothing to download in that 格式). Three in a row with the same network / TLS / rate-limit / component / disk code stop the rest of the batch (`BatchQueue`), and the status line says 「連續 3 部無法加入，已停止加入其餘 N 部：<reason>」 |

## Failure mode: a new yt-dlp is downloaded but not used (`ytdlp.package.activate result=NeedsRestart`)

**Symptom:** 修復, 一鍵更新 or 重新下載元件 finishes downloading, then asks for a restart
(「新的 yt-dlp 元件已下載，重新啟動程式後生效」).

**Read the `error=` field.** `Access to the path '…\bin\yt-dlp.new' is denied` (or
`…\bin\yt-dlp`) means a file inside that folder was open while it was being renamed.
Windows refuses to rename a directory while ANY file in it is open, even with full
sharing, which is how antivirus scans a file. v1.5.0 retries each rename for up to 10 s
(`YtDlpLayout.DefaultSwapWait`); still failing after that means something held the
files longer: a running download (yt-dlp.exe in use), or a scanner that is slow on this
machine. The package stays in `bin\`, and the next launch expands it in the background
before anything uses yt-dlp, so a restart is a complete fix. Log 2026-11-04,
before the retry existed: extraction took 16 s under real-time scanning, then the
rename failed instantly.

**Startup expands in the background (v1.5.0).** The first launch after an update shows
the window first (8 s instead of 28 s on the machine above); a parse started meanwhile
waits with 「元件首次啟動中，請稍候…」 until `ytdlp.package.activate trigger=startup` has
been logged and the warm-up probe has run.

## Failure mode: a large download fails part-way with HTTP 403 (E-AUTH001)

**Symptom:** a 1080p download stops at a few percent with 「YouTube 拒絕了這次請求…」,
and the same link works when queued again.

**In the log:** `download.failed error_code=E-AUTH001` whose stdout tail shows progress
lines (data arrived) before `ERROR: unable to download video data: HTTP Error 403:
Forbidden`. YouTube revoked the stream part-way. v1.5.0 re-runs such a download once
(`ytdlp.download.retry`): a second run re-extracts fresh URLs and `--continue` resumes
the partial file. A 403 before any progress is a real refusal and is not retried.
Measured 2026-11-04: 1 of 7 large downloads hit it, at 7.8%. yt-dlp also warns on
every call that YouTube extraction without a JavaScript runtime is deprecated; if
mid-stream 403s become frequent, see the JS runtime entry in 08-known-limitations.

## Failure mode: every parse fails with E-BLOCK01 after ~27 s (「處理元件無法啟動」)

**Symptom:** pasting any URL ends, roughly 27 seconds later, in 處理元件無法啟動：可能被
防毒軟體或系統政策阻擋…. Often it *used to work* — the first few launches succeed,
then every one fails.

**In the log:**
```
ytdlp.invoke.end op=metadata exit_code=-1 pid=0 start_ms=0 first_output_ms=(none) stdout_bytes=0 stderr_bytes=0
url.parse.failed category=ComponentMissing error_code=E-BLOCK01 elapsed_ms=27395 details="...Process.Start failed: 存取被拒..."
```
`pid=0` + `start_ms=0` + Access denied means `Process.Start` itself was refused —
yt-dlp never ran a line of code. `elapsed_ms` in the high twenties is the agent
holding the launch while it decides.

**Root cause (fixed in v1.5.0):** an endpoint-protection agent's behaviour monitoring
refusing a binary it has no reputation for. v1.3.0–v1.4.0 shipped a yt-dlp.exe we
compiled ourselves (to carry the AllowUntrustedCertificates hook): unsigned, a hash
nobody else in the world has. The agent tolerated it for a while, then switched to
blocking every launch. v1.5.0 ships upstream's official build byte-for-byte — see
design commitment #13 — and moved the hook into a yt-dlp plugin.

**How it was proven (repeat this if it ever comes back):** launch the shipped
`bin\yt-dlp\yt-dlp.exe --version` and a freshly extracted official `yt-dlp_win.zip`
alternately, four times each, the way ProcessSandbox does (UseShellExecute=false,
CreateNoWindow, redirected streams). 2026-11 result on an affected machine:
official 0.70–0.87 s every time; the self-built one "Access denied" after
26.4–27.6 s every time, with one entry per refusal in the agent's own
behaviour-monitoring log.

**If it happens with the official build:** the agent's policy now blocks yt-dlp by
name or behaviour, not by reputation. Nothing inside the app can fix that; the
machine's IT needs an exception for the portable folder.

## Failure mode: UI lag — dropdowns, sliders, hover all feel delayed

**Symptom:** interactive controls react late, startup feels slow, the process sits at
~100% of one CPU core while idle (Task Manager shows ~25% on a 4-core PC).

**Root cause (fixed in v1.5.0):** the pre-v1.5.0 UI animated a full-window gradient
forever, under cards carrying DropShadowEffect blur, and `ApplyRenderMode` forces
software rendering on every Intel HD/UHD GPU (see the next section). In software
mode the CPU redrew the whole window 60 times a second, and every interaction
queued behind those frames. Measured 2026-11: same build, animation on → 95–100% of
a core; animation off → 0.0%. v1.5.0's flat dark UI has no animations at rest, no
effects and no large translucent layers (design commitment #9). Measured on the same
software-rendered machine: idle 0.5% of a core and 127 MB working set (v1.4.0: 95–100%
and 240 MB); during a download the app itself used 0 ms of CPU over 8 s, because
progress is throttled to 4 updates/s per job and posted with BeginInvoke.

**If it comes back:** look for anything that animates while idle (an indeterminate
ProgressBar left running while collapsed counts), any `Effect`, or `Opacity`/
translucent brushes over large areas. Measure with the process's
`UserProcessorTime` delta over ~10 s of idle; anything above a few percent is a bug.

## Failure mode: everything that touches a file is slow — saves freeze the window, small downloads take ~40 s

**Symptom:** a 19-second clip takes 35–45 s from 下載 to 已完成; the row sat at
「80% · 115 KB/s」 for 20–30 s (v1.5.0: 「80% · 處理中…」); before v1.5.0's fix the window
also froze for ~6 s after every 下載 click.

**In the log:** nothing looks wrong. `ytdlp.invoke.end op=download` is late although the
progress lines show the streams arrived within a second; `media.download.completed` →
`download.completed` used to be ~6 s for an .mp4 (the rename of `<title>.media.mp4`).

**Root cause:** endpoint security holds individual file operations for a fixed ~3 s:
a rename over an existing file (File.Replace ≈ 6 s, File.Move-overwrite ≈ 3 s, Python's
`os.replace` ≈ 3 s), and operations by unsigned processes on .mp4 files. Measure it
without the app: time `File.Move(tmp, existing, overwrite: true)` against
`File.WriteAllText(existing, …)` in the same folder (3000 ms vs 2 ms on the development
PC); a fixed multiple of ~3 s whatever the file size is the signature.

**What v1.5.0 does:** writes config.json in place (+ `.bak`), writes the TLS plugin in
place, and lets yt-dlp write a download's final name directly when nothing follows it
(see 08, "File operations on PCs with endpoint security"). `--embed-thumbnail` still costs
~15–19 s per 影片 download there. **The real fix is outside the app:** an exclusion for
the portable folder and the save folder, or a signed build.

## Failure mode: 「處理元件的檔案已損毀」 (E-COMP002)

**Symptom:** every parse or download fails at once with E-COMP002; the health banner
offers 修復.

**In the log:** `Process.Start failed: 元件檔案已損毀（不是有效的 Windows 程式）— yt-dlp.exe`
(or ffmpeg.exe). `ProcessSandbox` checks the file is a PE image before starting it
(`PeImage`), because a damaged exe that still begins with a DOS "MZ" header made Windows
itself show an "Unsupported 16-bit application" box on every attempt. Win32 errors 193
and 216 from CreateProcess map to the same message.

**Fix:** 修復, or 設定 → 進階 → 重新下載元件. If it comes back right after a repair, the
download is being cut short (proxy, disk) or something rewrites the file.

## Log events added in v1.5.0's last round

- `ytdlp.tls_plugin removed=True|False error=…` — the 允許不受信任憑證 setting is off
  and the plugin folder was deleted (or could not be).
- `ytdlp.package.activate … error=無法刪除 yt-dlp-pkg.zip：…` with a success result — the
  package was put in place but could not be removed; later launches only retry the
  removal (the expanded folder carries `.package-sha256`).
- state.log `LinkPending` / `LinkSettled` — playlist / multi-link links waiting to be
  parsed (BatchJournal); open ones are offered at the next start.
- `download.name_in_use suffix=_2` — another running download held this name; this one
  was saved as `<title>_2` (OutputNames).
- `download.progress.listener_failed` — something that listens to progress threw; logged
  once per download and ignored (progress runs on yt-dlp's reader thread, where an
  unhandled exception would end the app).

## Failure mode: 「下載卡住」 (E-STUCK01) right after answering the 檔案已存在 dialog

**Symptom:** the user re-downloads a file that already exists, the conflict dialog
appears, they answer it after a while — and the row immediately fails with 下載卡住.

**In the log:** `download.started`, then `download.stuck timeout_s=90` while the dialog
was still open, then `ytdlp.invoke.end op=download cancelled=true first_output_ms=(none)`
the moment they clicked.

**Root cause (fixed in v1.5.0):** the executor shows the dialog from inside a running
job, and DownloadQueue's no-progress watchdog counted the time the user spent deciding
as a stall. It cancelled the job's token after 90 s; the choice then launched yt-dlp
with an already-cancelled token. The executor now reports
`DownloadProgressSnapshot(..., WaitingForUser: true)` before the dialog and an ordinary
report after it; the watchdog skips its check while waiting. Guarded by
`DownloadQueue_WaitingForUser_DoesNotTripTheWatchdog`.

## Failure mode: White window of doom (Intel HD/UHD iGPU)

**Symptom:** app starts, window appears, but window content is completely
white/blank. No error dialog. Log up to `render.mode mode=hardware tier=2`
looks normal.

**Root cause:** WPF hardware rendering on Intel HD/UHD Graphics drivers
(pre-Iris Xe) produces an empty frame buffer when the visual tree
contains DropShadowEffect + multi-layered LinearGradientBrush. Driver
returns no error so `RenderCapability.Tier` lies and reports 2.

### Diagnostic

In v1.2.1+, look at these log lines together:
```
gpu.detected names="Intel(R) HD Graphics 630"
render.mode mode=hardware reason=hardware tier=2
```
↑ This combination = bug. The known-bad-gpu fallback should have
triggered. If it didn't, the GPU name didn't match our regex — extend
`KnownBadGpuRegex` in `App.xaml.cs`.

In v1.2.0 and earlier, only `render.mode mode=hardware tier=2` is logged.
No GPU info. **Confirm by asking user to run:**
```powershell
Get-CimInstance Win32_VideoController | Select-Object Name, DriverVersion
```

### Fix path

| Version | Action |
|---|---|
| v1.2.1+ | Should be automatic. If still white, drop `software-render.flag` next to exe. |
| v1.2.0 and earlier | Manual `software-render.flag` (won't work — only v1.2.1+ reads it). Manual config.json edit `"ForceSoftwareRendering": true` to whichever DataRoot is actually used (check `config.path` event in v1.2.1+, or `%LOCALAPPDATA%\YtDlpTool\config.json` as fallback). Or upgrade to v1.2.1. |

### Why v1.2.1's chain has so many fallbacks

The config.json approach failed in testing — TestWritable probe
under endpoint security software can mis-classify the install dir as non-writable, so config.json
silently lives at `%LOCALAPPDATA%\YtDlpTool\config.json` instead of next
to the exe. User edits the wrong file, change is ignored. The flag-file
approach has zero path-resolution: it's checked at `AppContext.BaseDirectory`
which is unambiguously the exe folder.

## Failure mode: HTTPS verification failure (E-SSL01 / E-SSL02)

**Symptom:** download or URL parse fails. Error code is `E-SSL01` or
`E-SSL02`. stderr contains one of:
- `EE certificate key too weak (_ssl.c:1007)` — short-key leaf cert
- `self-signed certificate in certificate chain (_ssl.c:1010)` — chain
  validation can't find a trusted root
- generic `CERTIFICATE_VERIFY_FAILED`

### What v1.3.0+ does automatically

When E-SSL01 or E-SSL02 fires and `AllowUntrustedCertificates` is false,
v1.3.0 pops a modal warning offering to enable the bypass + auto-restart.
User accept → flag enabled, app relaunches, next attempt should work.

### What to check in log

```
config.loaded ... AllowUntrustedCertificates=False/True
ca-bundle.entries local_machine_count=N current_user_count=M total_count=K
ytdlp.invoke.begin op=... extra_env=...,YTDLP_RELAX_SECLEVEL allow_no_cert=true/false
```

- `AllowUntrustedCertificates=True` + `extra_env` includes
  `YTDLP_RELAX_SECLEVEL` → bypass active, should be working
- `AllowUntrustedCertificates=True` but `extra_env` MISSING
  `YTDLP_RELAX_SECLEVEL` → bug in YtDlpRunner.BuildExtraEnv
- `total_count` very small (e.g. < 50) → CA bundle empty / store
  permissions issue
- `config.changed key=AllowUntrustedCertificates` history → check when
  the flag last toggled and why (`trigger=settings_dialog_save` vs
  `ssl_failure_prompt`)

### When v1.3.0's bypass also fails

If after enabling, the same E-SSL error still appears, the failure is
NOT in Python ssl any more. Possible causes:

1. yt-dlp internally uses `curl_cffi` (libcurl-based, C-level TLS not
   covered by our Python-level wrap_socket hook). Check stderr for
   `curl_cffi` / `impersonate` strings.
2. TLS issue below the Python layer (mTLS / client cert required;
   check `clientHelloHandshakeFailure`-style errors).
3. Network blocking the actual HTTP request payload, not just TLS.

### Manual diagnostic command

Have the user run yt-dlp directly with verbose to see the raw failure:

```pwsh
cd <install-folder>
$env:YTDLP_RELAX_SECLEVEL = '1'
$env:SSL_CERT_FILE        = "$PWD\system-ca-bundle.pem"
$env:REQUESTS_CA_BUNDLE   = $env:SSL_CERT_FILE
$env:CURL_CA_BUNDLE       = $env:SSL_CERT_FILE
$env:OPENSSL_CONF         = "$PWD\openssl-permissive.cnf"

.\bin\yt-dlp.exe --no-check-certificates -v -F "https://www.youtube.com/watch?v=..." 2>&1 | Tee-Object "$env:TEMP\ytdlp-verbose.log"
```

The `-v` output names the HTTP client backend used and the exact ssl
context settings.

---

## Failure mode: "URL解析失敗 (錯誤訊息為空白)"

### Diagnostic table

Look at the v1.1.19+ `ytdlp.invoke.end` event for the failed call:

| `first_output_ms` | `stdout_bytes` | `stderr_bytes` | `timed_out` | Hypothesis |
|---|---|---|---|---|
| `(none)` | 0 | 0 | true | **yt-dlp hung in Python startup.** Past culprits: env-clear (v1.1.18 fixed, v1.1.27 regressed, v1.3.6 re-fixed), invalid stdin (v1.1.18 fixed), `%TEMP%` on a network share (v1.3.6 guard), AV scanning a fresh PyInstaller extraction. |
| < 1000 | small | small/0 | true | yt-dlp started OK but the FIRST network call hung. Likely: DNS for `googlevideo.com` subdomains, or proxy required but not detected. |
| > 5000 | small/0 | growing | true | TLS handshake hung. SSL inspection without trusted CA. |
| any | large | small | true | Fragment retry loop / rate-limited. Look at `RecentStdout` (in error details) for the actual retry messages. |
| any | any | non-empty | false | Real yt-dlp error. Read stderr. |

### Read these two lines BEFORE anything else

Two log lines settle most of this failure mode in seconds:

**1. `version.probe`** — if it reads `ytdlp=(probe-failed) ffmpeg=<version>`,
you are done looking at the network. `--version` performs zero network I/O,
and ffmpeg.exe went through the *same* `ProcessSandbox` successfully. Up to
v1.3.8 the difference was that yt-dlp.exe was PyInstaller onefile
(self-extracting ~30 MB to a fresh `%TEMP%\_MEIxxxxxx` on **every** run, so AV
rescanned it every time — it never "warmed up"); v1.4.0 moved to onedir. In
v1.3.0–v1.4.0 it was also a self-built binary that endpoint security distrusts —
see the E-BLOCK01 section above; v1.5.0 ships upstream's build. ffmpeg.exe is
native.

v1.3.6 surfaces this to the user directly via `health.ytdlp ok=false` + the
red startup banner, instead of letting them discover it 38 seconds into their
first parse.

**2. `start_ms`** (v1.3.6+, on `ytdlp.invoke.end`) — ms spent inside
`Process.Start` itself. The timeout watchdog only starts counting *after*
Start returns, so this is wait time the "[timeout after 30s]" message never
accounted for; a 30s timeout genuinely taking 38s is `start_ms` plus the
kill/pipe-drain. A large `start_ms` means endpoint security scanning on CreateProcess or
a cold read from slow storage.

### Concrete next steps

If `(none) / 0 / 0 / true` — have the user open PowerShell in the portable
folder's `bin` and run:

```pwsh
Measure-Command { .\yt-dlp.exe --version } | Select-Object TotalSeconds
echo $env:TEMP
```

Do **not** hand them a `cmd /c "echo %time% && … && echo %time%"` one-liner to
time it — cmd expands both `%time%` occurrences when it parses the whole line,
so they come back identical and the elapsed time reads as 0.00s regardless.

Compare:
- **Answers in ~1s** → yt-dlp is fine in a normal environment; the difference
  is what *we* hand the child. Diff `ConfigureSandboxedEnvironment` against
  what a normal shell gives it.
- **Takes 30s+** → slow everywhere. Check whether `$env:TEMP` is a UNC path or
  a mapped network drive (v1.3.6 auto-falls-back to LocalApplicationData and
  logs it), then AV exclusions.
- **Same hang, no output** → yt-dlp.exe itself is broken on their machine.
  Check Windows Defender quarantine history.

### Past root causes (don't re-debug)

- v1.1.18 → **regressed in v1.1.27** → re-fixed in v1.3.6:
  `info.EnvironmentVariables.Clear()` stripped vars PyInstaller-Python needed.
  `91b2a62` removed the Clear(); `a4c0668` ("revert(arch): drop TTY-mode
  complications, base on v1.1.16 pipe-mode") put it back as collateral damage —
  that revert was justified by an unrelated finding (the reporter's real root
  cause was broken IPv6 routing, fixed by `--force-ipv4`) and swept up the env
  work with the TTY work it was never part of. It cost a full re-investigation
  in 2026-08. **Fix**: inherit the parent environment, subtract only
  `StrippedEnvVars` (PYTHONPATH/PYTHONHOME/…), still override PATH.
  **Guarded by** `Run_InheritsParentEnvironment`,
  `Run_InheritsWindowsCoreVarsPyInstallerNeeds`, `Run_StripsPythonHijackVars`
  in `ProcessSandboxTests`. If you are about to re-add an env allowlist, read
  the comment block above `ConfigureSandboxedEnvironment` first.
- v1.1.18: inherited GUI-parent stdin handle caused isatty() hang.
  **Fix**: `RedirectStandardInput=true; StandardInput.Close()`.
- v1.1.14: managed environment had no `HTTP_PROXY` in env but system proxy
  was required (set via WPAD/PAC). **Fix**: `SystemProxy.DetectHttpProxy`
  now does WinHTTP WPAD/PAC resolution.

## Failure mode: "下載失敗 (錯誤訊息為空白)"

Same diagnostic table as above, but read the `download` op events instead
of `metadata`.

Special case: fragment retry loop. In v1.1.8 yt-dlp's silent retry loop
exceeded our 90s watchdog with no stderr output. Fixed by:
- Keeping `--newline` for line-buffered stdout
- KEEPING warnings on the download path (we suppress them only on metadata)
- 3 retries × 3s instead of 10 × 5s
- `ProcessSandbox` keeps last 30 stdout lines in a ring buffer that's
  attached to ProcessExitInfo.RecentStdout for diagnostics

If the user sees fragment-retry failures, ask them for the `details=` in
the `download.failed` log entry — it should contain the stdout-tail with
the actual yt-dlp retry messages.

## Failure mode: "字幕 + 擷取片段組合不出來"

This was a thing in early v1.1.x. Resolution architecture:

1. yt-dlp downloads the full media (no `--write-subs`)
2. yt-dlp downloads subs separately (`--skip-download --write-subs --write-auto-subs`)
3. ffmpeg clips the media if a clip range was requested
4. ffmpeg muxes subs into the (clipped or not) MP4 as soft-subtitle tracks

If subs are present but missing from final file:
- Check `YtDlpDownloadExecutor` step 2 succeeded (look for the subs
  download op in log, exit_code=0)
- Check ffmpeg mux step (`FfmpegRunner.MuxSubsAsync`) succeeded
- A common cause: video has only auto-captions in the requested language;
  user expected real subs. Check `manifest.subtitles[]` in metadata response.

## Failure mode: "白色UI / 空白UI / 啟動就閃退"

### White / blank UI (theme rendering issues)

- Check `render.mode` log entry. If `mode=software-forced` or
  `mode=software-auto`, hardware acceleration is off. Software rendering
  can interact badly with custom WPF effects (DropShadow, blur). The pre-v1.5.0
  Aurora Glass theme used DropShadow heavily; v1.5.0 uses no effects at all
  (design commitment #9), so a white window there points at the driver, not
  at our visuals.
- v1.1.x ships with `Config.ForceSoftwareRendering = false` and an
  auto-fallback when `RenderCapability.Tier == 0`. managed environments can
  force software rendering via GPO; that's the common path.

### Single-machine crash on startup

The user gets a MessageBox with "崩潰報告已存至" — that's the early-diag
log at `%TEMP%/YtDlpTool-startup-*.log`. Always ask for that file.

Common causes:
- StateJournal lock contention → "two instances open" (rare but happens
  on Roaming Profile setups where two folders share state)
- WPF XAML parse exception → resource type mismatch (see commitment #11)
- Theme service init failure → ResourceDictionary URL typo

### White borders around content (managed environment)

(User-reported, May 2026, currently investigating.) Suspected:
- Some panel's `Background` not bound to `DynamicResource` — falls back
  to Window's default white
- DropShadowEffect missing in software-rendering mode → outer halo
  becomes visible as a hard edge
- DPI scaling at 125% / 150% causing layout rounding artifacts

To diagnose: ask for screenshot, ask for `render.mode` value, ask if same
machine in unmanaged context shows same artifact.

## Failure mode: "更新失敗 / 找不到新版本"

Look at `update.check.*` events.

| Log line | Meaning |
|---|---|
| `update.check.api.no_response` | GitHub unreachable. Network or proxy. |
| `update.check.api.status_4xx code=403` | Rate-limited. 60/h per IP unauthenticated. Rare unless user keeps mashing "立即檢查". |
| `update.check.api.status_5xx` | GitHub down. Transient. |
| `update.manifest.verify_failed` | Manifest signature invalid. Either tampering, or our embedded Fulcio root needs updating. |
| `update.check.no_newer` | Genuinely up to date. |

For `verify_failed`, additional fields:
- `step=parse_bundle` → the .sigstore bundle is malformed
- `step=cert_chain` → leaf → intermediate → root broken (often: Fulcio rotated)
- `step=san_mismatch` → cert was issued by Fulcio for a different identity
- `step=signature` → signature math fails
- `step=rekor` → transparency log proof invalid

## Failure mode: "下載完成但檔案開不了"

- ffmpeg merge failed silently. Check `FfmpegRunner` events.
- Common: user has limited disk space and the merge step ran out of room
  for the intermediate file. We don't currently pre-check disk space;
  if this becomes a regular complaint, add a check in
  `YtDlpDownloadExecutor.PrepareSaveDirectory`.

## Tools to install before deep debugging

- **Process Explorer** (Sysinternals): inspect the actual environment block
  of yt-dlp.exe child while it's running. Useful to verify our ExtraEnv
  injection landed.
- **Procmon** (Sysinternals): file/registry/network activity. Use sparingly
  — generates massive logs.
- **Fiddler** or **mitmproxy**: when SSL inspection is suspected. Note:
  installing these on the user's managed machine may itself be blocked.
- **DebugView** (Sysinternals): catch `OutputDebugString` traces. We don't
  emit any but Windows libraries sometimes do.

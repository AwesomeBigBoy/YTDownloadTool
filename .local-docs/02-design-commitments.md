# Design commitments

These are the **rules** the codebase follows. Many cannot be derived by reading
any single file — they're load-bearing decisions, often born from a specific
incident. Breaking them silently re-opens fixed bugs. If you propose changing
any of these, treat it as an architectural change, not a tweak.

---

## 1. Delegate everything YouTube-specific to yt-dlp. Do not override its decisions.

> Source: v1.1.10 → v1.1.11 incident — hard-coded `--extractor-args player_client=...`
> broke metadata fetch when YouTube changed PO Token requirements.

- **Never** pass `--extractor-args` to yt-dlp.
- **Never** hard-code a player client list.
- **Never** parse YouTube URLs beyond the strict input validator (which only
  checks "is this a YouTube URL at all", not "which kind").
- When YouTube breaks something, the fix is "update yt-dlp" via the in-app
  redownload-components button, not a code change in this repo.

The architectural promise to the user (verbatim, from a real bug report):
> "未來只要更新 yt-dlp 與 ffmpeg 就可以繼續使用"

If you find yourself writing logic that decides which YouTube extractor
strategy to use, stop. yt-dlp's default behavior evolves with YouTube; ours
should not.

## 2. WPF + self-contained single-file publish. NOT NativeAOT.

> Source: original architectural decision — WPF and NativeAOT are incompatible
> at the time of build. `<PublishAot>true</PublishAot>` fails for any project
> that uses XAML.

- Publish config: `dotnet publish -c Release -r win-x64` produces a single
  ~70 MB exe with compression.
- Trimming is **off** for WPF (it relies on reflection-discovered types).
- If a future .NET release makes WPF + AOT work, that is a major version bump
  not a patch.

## 3. Portable folder, no installer.

> Source: target environment is managed Windows where AppLocker / WDAC
> blocks installers and users cannot elevate.

- Everything lives next to the .exe.
- No registry writes. No `%ProgramFiles%`. No HKEY_LOCAL_MACHINE.
- Per-user state stays in the portable folder too — multiple users on one
  machine each get their own copy. This is intentional; we'd rather waste
  some disk than fail in AppLocker'd shared environments.

## 4. Sigstore keyless signing, NOT Authenticode.

> Source: v1 scope decision. Authenticode certs cost money + require key
> management; Sigstore is free, identity-bound to the GitHub Actions OIDC.

- Release artifacts are signed by `cosign` running inside `.github/workflows/release.yml`.
- The signing identity is the workflow file path + tag pattern, locked in
  `SigstoreVerifierOptions.ExpectedSanRegex`.
- The Fulcio root + intermediate certs are **embedded in the binary** at
  `src/YtDlpTool.Domain/Security/SigstoreRoots.cs`. Updating those requires
  a new build.
- Tradeoff accepted: first-launch SmartScreen warning. Documented in public
  README. Acceptable for v1; revisit if user volume justifies $$$.

## 5. Sandboxed PATH, but do NOT clear child env.

> Source: v1.1.18 incident — `info.EnvironmentVariables.Clear()` made yt-dlp
> hang on managed hosts because PyInstaller-frozen Python depended on
> env vars not in our small whitelist.

- `ProcessSandbox.ConfigureSandboxedEnvironment` only overrides:
  - `PATH` (security: this IS the hijack vector)
  - `PYTHONIOENCODING`, `PYTHONUTF8` (deterministic output)
- Everything else inherits from the parent process unchanged.
- Callers may force specific values via `ProcessStartArguments.ExtraEnv`
  (applied last, wins over inheritance).

**Do not "improve" this by stripping env vars again.** The previous strict
whitelist looked safer but was actively breaking production. The risk surface
of inheriting parent env is small in this app (we own the parent, no untrusted
processes set our env).

## 6. Stdin must be redirected and closed for every child.

> Source: v1.1.18 incident — GUI parent has no attached console, child
> inherited an invalid stdin handle, PyInstaller-frozen Python hung doing
> isatty().

```csharp
RedirectStandardInput = true;
// then immediately after Process.Start:
try { process.StandardInput.Close(); } catch { }
```

Pattern applies to **every** ProcessSandbox-launched child, not just yt-dlp.
ffmpeg has the same issue.

## 7. Subtitle download is a separate yt-dlp invocation.

> Source: v1.1.13 incident — combining `--write-subs` with media download
> on a JS-runtime-less host (no deno/node) caused yt-dlp to take a heavier
> player_response path that rate-limited and failed.

- `YtDlpRunner.DownloadAsync` never passes `--write-subs`.
- `YtDlpRunner.DownloadSubtitlesOnlyAsync` is the **only** path that writes
  subtitle files (`--skip-download --write-subs --write-auto-subs`).
- Subtitle muxing into MP4 is `ffmpeg`'s job, not yt-dlp's.

## 8. Clip extraction is ffmpeg's job, not `--download-sections`.

> Source: v1.1.12 incident — `--download-sections` requires a JS runtime
> to deobfuscate section URLs on current YouTube. Without one (and we
> deliberately don't ship a JS runtime), section download silently hangs.

- Two-pass: yt-dlp downloads the full file, ffmpeg stream-copies the
  requested range (`-c copy -ss <start> -to <end>`).
- Marginally slower than `--download-sections` on big files but **works
  on every host configuration we support**.
- Stream-copy means no re-encoding — lossless and fast.

## 9. UI theme uses DynamicResource, not StaticResource.

> Source: v1.1.x theme switching incident — theme swaps at runtime require
> DynamicResource binding. StaticResource resolves at parse-time and never
> updates.

For any resource that depends on the theme (brushes, colors, but not raw
doubles like spacing), use:

```xml
<Border Background="{DynamicResource SurfaceBrush}" ... />
```

Not:

```xml
<Border Background="{StaticResource SurfaceBrush}" ... />
```

`ThemeService.Apply` swaps the merged dictionary at runtime; DynamicResource
is the only thing that picks up the change.

## 10. Log files never contain raw URLs, titles, or user-identifying data.

> Source: privacy spec §5.4 + an early concern that log files end up in bug
> report screenshots.

- URLs → `AppLogger.HashSuffix(url)` (first 8 hex chars of SHA-256)
- Titles → never logged at all; only video IDs after hashing where needed
- Proxy URLs → `AppLogger.HashSuffix(proxy)` (so internal proxy hostnames
  do not leak)
- File paths inside the user's folders → `Paths.DataRoot`-relative when
  possible, full path only when essential (e.g., CA bundle generation)

If you're tempted to log `videoTitle` or `youtube.com/watch?v=...` "just for
debugging", **do the structured ID-hash instead**. There's no escape valve.

## 11. WPF resource type mismatches compile clean but explode at runtime.

> Source: v1.0.x bug — `<sys:Double>` resource was being assigned to a
> `Thickness` property; build was green, runtime XAML parse threw.

When changing or adding resources in `Theme.xaml` / `Light.xaml` / `Dark.xaml`:
- Verify the consumer's expected type. `Margin` expects `Thickness`, not
  `Double`. `Background` expects `Brush`, not `Color`.
- Headless CI cannot catch these. **You must run the app and exercise the
  views that use changed resources before tagging a release.**
- A memory entry `wpf-runtime-xaml-blindspot` captures this rule.

## 12. State journal lock is per-portable-folder, intentional.

> Source: `StateJournal..ctor` opens `state.log` with exclusive `FileShare.None`.

- Two `YtDlpTool.exe` instances pointing at the same portable folder cannot
  both run. The second one fails at startup with a friendly message.
- This is a feature: prevents the queue from being corrupted by two writers.
- If you ever need multi-instance support, redesign StateJournal first (probably
  switch to a SQLite WAL-mode journal). Do not loosen the lock.

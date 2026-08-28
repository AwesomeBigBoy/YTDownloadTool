# Known limitations and deferred work

Things we know are not great. Some are deferred to a future major version;
some are genuinely "won't fix" by design; some are open questions.

## v1.3.0 SSL bypass — explicit trade-off (not a bug)

### What it is

`AllowUntrustedCertificates = true` activates `YTDLP_RELAX_SECLEVEL=1`
which causes our PyInstaller-built yt-dlp.exe to disable HTTPS
verification entirely (no issuer check, no key-length check, no
hostname check). Necessary to operate on networks whose inspection
proxy presents short-key leaf certs that Python's OpenSSL default
SECLEVEL=1 rejects at handshake.

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

### Playlists
- **Why deferred:** spec restricted v1 to single-URL flow for UI simplicity.
- **Status:** `--no-playlist` is passed on every yt-dlp call. Playlist
  URLs are NOT rejected by the URL validator but only the first video
  in a playlist is fetched.
- **Path forward:** new "playlist" mode in URL validator + new UI surface
  for picking videos from a playlist.

### Re-encoding controls
- **Why:** clip is `-c copy` (stream copy, lossless), audio re-encode
  to mp3 happens only when user picks audio-only mode and the source
  is not already m4a/aac.
- **Status:** no UI for picking codec or bitrate.
- **Path forward:** advanced settings panel if users ask. Most don't.

## Won't fix (by design)

### JS runtime for yt-dlp extractor
- **Reason:** would inflate distribution by 50-100 MB (deno or node),
  and would tie us to a specific runtime version that yt-dlp recommends
  but doesn't strictly require.
- **Tradeoff accepted:** we lose some formats/features that need JS
  deobfuscation. yt-dlp prints a warning; we let users see it.
- **If this becomes a problem:** revisit, but probably as an optional
  "advanced" download in the in-app updater (not bundled by default).

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

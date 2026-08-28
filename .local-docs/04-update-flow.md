# In-app update flow

End-to-end trace of "user clicks the update banner" — what the code does and
what gets verified at each step.

## The contract with the user

> The app, yt-dlp, and ffmpeg can all be updated **without the user doing
> anything except clicking one button**, and the update is **safe** even on
> an untrusted network.

"Safe" here means: an attacker who controls the network (proxy, DNS, even
the GitHub mirror cache) cannot trick the user into running a different
binary than the one the legitimate maintainer signed.

## The signing identity

Every release artifact is signed by the GitHub Actions OIDC identity of
the `release.yml` workflow. The verifier (in the client) is hard-coded to
accept ONLY:

```
Issuer:   https://token.actions.githubusercontent.com
SAN regex: ^https://github\.com/AwesomeBigBoy/YTDownloadTool/\.github/workflows/release\.yml@refs/tags/v.*$
```

This means:
- Signatures from any other GitHub org → rejected
- Signatures from any other workflow in this repo → rejected
- Signatures from a non-tag push (e.g., a manual workflow_dispatch on a branch) → rejected
- Signatures from PRs (which use a different SAN) → rejected

Where the regex lives: `src/YtDlpTool/AppHost.cs` — `SigstoreVerifierOptions.ExpectedSanRegex`.

## Frequency

`AppConfig.AppCheckFrequency` (UI: Settings → 更新):
- `Never`
- `EveryLaunch` (default since v1.2.2; was `Weekly` in v1.0–v1.2.1)
- `Daily`
- `Weekly`
- `Monthly`

`YtDlpCheckFrequency` and `FfmpegCheckFrequency` also default to
`EveryLaunch` since v1.2.2 — though note that the actual gate in
`AppHost.StartBackgroundUpdateCheckAsync` only consults
`AppCheckFrequency`; per-component frequencies surface in Settings for
informational/manual-check use only.

`AppHost.StartBackgroundUpdateCheckAsync` waits 5 seconds after startup
(was 60s pre-v1.2.2; the long delay made the check effectively never
fire in short sessions once the cadence became EveryLaunch), then checks
if elapsed-since-LastAppCheck exceeds the configured interval. If so,
queries GitHub and shows the banner if a newer version exists.

There's also a "立即檢查更新" button in Settings that bypasses the cadence
gate.

## Step-by-step trace

### Step 1: Discover the latest release

`UpdateChecker.CheckAsync`:

```
GET https://api.github.com/repos/AwesomeBigBoy/YTDownloadTool/releases/latest
   User-Agent: YtDlpTool/<this version>
   Accept: application/vnd.github+json
```

GitHub returns JSON listing assets. We extract URLs for `manifest.json`
and `manifest.json.sigstore`. Note: we do NOT trust the response yet —
GitHub itself could be MITM'd. The manifest's signature is what we trust.

### Step 2: Download and verify the manifest

```
GET <manifest.json>          → into memory (bytes)
GET <manifest.json.sigstore> → into memory (bytes)
```

Verify the manifest signature via `SigstoreVerifier`:
1. Parse the .sigstore bundle (cosign legacy format: `base64Signature`,
   `cert`, `rekorBundle`).
2. Verify the certificate chain: leaf → Fulcio intermediate → Fulcio root.
   The intermediate + root PEMs are embedded in
   `src/YtDlpTool.Domain/Security/SigstoreRoots.cs`.
3. Verify the leaf cert's SAN matches `ExpectedSanRegex`.
4. Verify the leaf cert's issuer matches `ExpectedIssuer`.
5. Verify the signature over the manifest bytes.
6. Verify the Rekor transparency log inclusion proof:
   - Reconstruct the log entry
   - Verify Rekor's signature over the entry (Rekor pubkey also embedded
     in `SigstoreRoots.cs`)

If any step fails: abort, show "簽章驗證失敗", log
`update.manifest.verify_failed`.

### Step 3: Read the manifest

`manifest.json` schema (camelCase, serialized from PowerShell — the C# DTOs
declare matching `[JsonPropertyName("camelCase")]` attributes):

```json
{
  "appVersion": "1.1.19",
  "ytdlpVersion": "2026.03.17",
  "ffmpegVersion": "8.1.1",
  "files": [
    { "name": "YtDlpTool.exe", "size": ..., "sha256": "...", "sigstoreName": "YtDlpTool.exe.sigstore" },
    { "name": "yt-dlp.exe",    "size": ..., "sha256": "...", "sigstoreName": "yt-dlp.exe.sigstore" },
    { "name": "ffmpeg.exe",    "size": ..., "sha256": "...", "sigstoreName": "ffmpeg.exe.sigstore" }
  ],
  "owner": "AwesomeBigBoy",
  "repo": "YTDownloadTool",
  "tagName": "v1.1.19"
}
```

### Step 4: Decide which files to update

`UpdateChecker` reads currently-installed versions:
- App version = assembly `Version`
- yt-dlp version = run `yt-dlp.exe --version` and parse first line
- ffmpeg version = run `ffmpeg.exe -version`, parse from first line

Compares each pair-wise. Files where remote > local go into `NewerFiles`.
If `NewerFiles` is empty, no banner is shown.

### Step 5: User clicks "立即更新"

`UpdateApplier.ApplyAsync`:

For each file in `NewerFiles`:

1. Download asset → temp file in `Paths.DataRoot/.update-cache/`
2. Download corresponding `.sigstore` → temp file
3. Verify signature over the downloaded file bytes — same procedure as
   Step 2, including identity + Rekor checks
4. Verify SHA-256 matches `manifest.files[].sha256` — this is belt-and-suspenders
   (the signature already covers integrity, but a mismatch here would indicate
   a manifest typo or a TOCTOU between manifest and download)
5. If all verifications pass: atomic rename into place (`File.Move(..., overwrite=true)`).
   For the running .exe, that means renaming the running file (Windows allows
   renaming a running .exe but not deleting/overwriting it without a handle dance)
   then dropping the new one in place. The actual swap-in-place pattern:
   - Rename current `YtDlpTool.exe` → `YtDlpTool.exe.old`
   - Move new file → `YtDlpTool.exe`
   - On next launch, `AppHost.CleanupStaleUpdateArtifacts()` deletes
     `*.old` files in `AppDirectory` + `BinDirectory`. **v1.2.2+ only.**
     Pre-v1.2.2 the doc claimed this happened but the cleanup code did
     not exist, so `.old` files accumulated forever in the install
     folder. (`UpdateApplier.cs:181-183` did call `File.Delete` on the
     backup after Move, but the running process still held the renamed
     file open so the call always failed silently. Cleanup must run in
     the *next* process, which is what `CleanupStaleUpdateArtifacts`
     does.)

6. Show "更新完成，需要重新啟動" toast → on confirm, `Application.Current.Shutdown()`
   + Process.Start the new exe.

### Step 6: Restart

The new exe boots up. `AppHost` ctor calls `CleanupStaleUpdateArtifacts`
which scans the install folder for `*.old` and deletes each. If any
deletion fails (Defender briefly scanning the freshly-replaced file),
it's logged via `update.cleanup.stale_failed` and the next launch will
retry — best-effort, never blocks startup.

## Failure modes and what they look like

| Failure | UI message | Log event | What it means |
|---|---|---|---|
| Network unavailable | "找不到最新版本" | `update.check.error category=Network` | DNS / 連線失敗 |
| GitHub API rate-limited | "找不到最新版本" | `update.check.error category=RateLimit` | unauth GitHub API gives 60 req/h per IP |
| Manifest signature invalid | "簽章驗證失敗" | `update.manifest.verify_failed` | Tampered manifest, or attacker MITM, or our embedded Fulcio root is out of date |
| Per-file signature invalid | "檔案驗證失敗" | `update.file.verify_failed name=...` | Single file tampered |
| SHA-256 mismatch | "檔案損毀" | `update.file.sha_mismatch` | Should be impossible if sig passes; indicates corruption mid-download or manifest bug |
| Disk full / write fail | "寫入失敗" | `update.apply.io_failed` | Insufficient space or AV interference |
| User cancelled mid-apply | "已取消" | `update.cancelled` | Partial state cleaned up |

## Why the embedded Fulcio root matters

Sigstore's CAs do rotate. The current root in `SigstoreRoots.cs` is the
Fulcio v1 root, which is good until at least 2030 by published policy.
**If Sigstore rotates the root before then**, this app's verification will
break and only a new app build can fix it.

Mitigation: every release ships with the current root. As long as users
update at least once between Fulcio rotations, they stay current.

If we wanted to support "the embedded root is too old, fetch a fresh one
from sigstore.dev" — that's a project for another day. Adding that
correctly requires another out-of-band trust anchor and significantly
complicates the threat model.

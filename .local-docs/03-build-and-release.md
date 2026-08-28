# Build and release

## Local build (development)

Prerequisites:
- .NET 8 SDK (pinned via `global.json`, currently `8.0.x`)
- Windows. The app is Windows-only because WPF is Windows-only.

```pwsh
dotnet restore
dotnet build YtDlpTool.sln -c Release
dotnet test  -c Release
```

Run the app locally:

```pwsh
dotnet run --project src/YtDlpTool -c Release
```

This does NOT bundle yt-dlp.exe / ffmpeg.exe — for the local-run case you
need to drop both into `src/YtDlpTool/bin/Release/net8.0/bin/` yourself, or
run `build/fetch-external-deps.ps1 -OutputDir <that bin folder>`.

## Single-file portable publish (manual, what users get)

```pwsh
dotnet publish src/YtDlpTool/YtDlpTool.csproj -c Release -r win-x64
```

Settings come from `YtDlpTool.csproj`:
- `<PublishSingleFile>true</PublishSingleFile>`
- `<SelfContained>true</SelfContained>`
- `<RuntimeIdentifier>win-x64</RuntimeIdentifier>`
- `<EnableCompressionInSingleFile>true</EnableCompressionInSingleFile>`
- `<PublishTrimmed>false</PublishTrimmed>` — WPF can't be trimmed

Output: `src/YtDlpTool/bin/Release/net8.0/win-x64/publish/YtDlpTool.exe`
(~70 MB compressed).

## Release pipeline (the only path users see new versions)

Trigger: `git push origin v<X.Y.Z>` (an annotated tag matching `v*`).

The workflow `.github/workflows/release.yml` runs on `windows-2022`:

1. **Checkout + Setup .NET 8** (pinned via global.json)
2. **Compute version from tag** — strips the `v` prefix, exports as
   `steps.ver.outputs.version`. Reads `build/external-deps.json` for the
   pinned yt-dlp + ffmpeg versions.
3. **Patch csproj** — injects `<Version>X.Y.Z</Version>` into the WPF csproj
   so the assembly metadata matches the tag.
4. **Restore / Test / Publish** — standard. Test failure aborts the release.
5. **Fetch external deps** — `build/fetch-external-deps.ps1` downloads
   yt-dlp.exe from the yt-dlp release and ffmpeg.exe from GyanD/codexffmpeg.
   Both pinned to specific versions.
6. **Assemble portable folder** — combines published .exe + bin/yt-dlp.exe
   + bin/ffmpeg.exe.
7. **Build manifest** — `build/build-manifest.ps1` writes `manifest.json`
   listing each file with its SHA-256.
8. **Install cosign**, **Sigstore-sign** the manifest and each of the three
   .exe files. Produces `.sigstore` bundle per file.
9. **Zip + GitHub Release** — idempotent: if a release for this tag exists
   (e.g., a partial previous attempt), `gh release upload --clobber`
   refreshes assets in place; otherwise creates the release first.

### Pinned external deps

`build/external-deps.json`:

```json
{
  "yt-dlp":  { "version": "2026.03.17", "sha256": "..." },
  "ffmpeg":  { "version": "8.1.1",      "sha256": "..." }
}
```

The SHA-256 is verified by `fetch-external-deps.ps1` after download. If the
upstream changes hashes (e.g., re-published a release), the workflow fails
loudly rather than silently shipping a different binary.

## Release artifacts (what ends up on GitHub)

For each tag `vX.Y.Z` the release page contains exactly 9 files:

```
YtDlpTool-vX.Y.Z-win-x64.zip       ← what users download
YtDlpTool.exe                       \
YtDlpTool.exe.sigstore               |
yt-dlp.exe                           |  individually fetched by
yt-dlp.exe.sigstore                  >  the in-app updater
ffmpeg.exe                           |  (NOT the zip)
ffmpeg.exe.sigstore                  |
manifest.json                        |
manifest.json.sigstore              /
```

The **zip** is a one-time download for new users; the in-app updater
**never** uses the zip — it fetches individual files per `manifest.json`
so we can roll a yt-dlp-only or ffmpeg-only update without re-shipping
the app.

## Cutting a release: the actual steps

```pwsh
# 1. Make sure main / feat/initial-implementation is clean and tests pass
git status
dotnet test -c Release

# 2. Decide a version. We use semantic-ish v1.1.X for the v1 line.
$TAG = "v1.1.20"

# 3. Tag with annotated message (the message becomes part of the GH release notes
#    visible on the release page — write it for users, not for internal context)
git tag -a $TAG <SHA> -m "v1.1.20

What changed — written for users, not for engineers.
Multiple paragraphs OK. No emoji."

# 4. Push the tag (this triggers release.yml)
git push origin $TAG

# 5. Watch:
#    https://github.com/AwesomeBigBoy/YTDownloadTool/actions/workflows/release.yml
#    ~8-12 minutes start to finish.
```

If the workflow fails partway and you fix-then-retry, **delete both the broken
GH release AND the remote tag first** — leaving a half-failed release with
some assets uploaded confuses the idempotent path. Tag name reuse on GitHub
can also be sticky due to immutable-release residual state; if `gh release upload`
422s on a fresh tag, bump the patch number rather than fighting it.

## SDK pin

`global.json` pins `8.0.x`. Don't bump without updating:
- `actions/setup-dotnet` consumer in both `release.yml` and `pr-check.yml`
- `<TargetFramework>net8.0</TargetFramework>` in every csproj
- Any test snapshot that captures .NET version

## CI / PR checks

`.github/workflows/pr-check.yml` runs build + tests on every PR.
`.github/workflows/codeql.yml` runs CodeQL on push to main and weekly.
`.github/dependabot.yml` opens dep-update PRs weekly (Monday 09:00 Asia/Taipei).

## What can break in the workflow

| Symptom | Likely cause | Fix |
|---|---|---|
| 422 "Cannot upload assets to an immutable release" | Repo setting `Enable release immutability` is on | Settings → General → Releases section → toggle off |
| 422 "Release ... does not exist" then immediate fail | Ruleset blocks tag creation | Settings → Rules → Rulesets → disable Restrict creations, or scope rules to main only |
| Test fails on `Run_EnvironmentIsWhitelisted` | Test asserts old contract; the env-clear was removed in v1.1.18 | Update test to assert new contract (PATH override + ExtraEnv passthrough) |
| Sigstore signing step fails | OIDC token unavailable | Most likely a misconfigured `permissions: id-token: write` in release.yml — verify it's present at the workflow level |
| External-deps fetch SHA mismatch | Upstream re-published a release with same version number | Update SHA in `build/external-deps.json`, or bump the version pin |

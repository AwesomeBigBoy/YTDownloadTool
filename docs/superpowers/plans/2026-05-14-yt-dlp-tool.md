# YtDlpTool Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a lightweight Windows YouTube downloader with Aurora Glass UI, hardened security model, and one-click Sigstore-verified updates, shipped as a portable folder.

**Architecture:** Three-layer C# .NET 8 application — WPF UI (NativeAOT-compiled exe), pure-C# Domain library, and Process layer wrapping `yt-dlp.exe`/`ffmpeg.exe` subprocesses. Update signatures use Sigstore keyless. Persistent state lives under `%LOCALAPPDATA%\YtDlpTool`.

**Tech Stack:**
- .NET 8 SDK, WPF, NativeAOT
- xUnit (test framework)
- `System.Text.Json` (source generator), `CommunityToolkit.Mvvm`, `CommunityToolkit.WinUI.Notifications`, `NSec.Cryptography`
- GitHub Actions + sigstore-action for CI/CD

**Spec:** `docs/superpowers/specs/2026-05-14-yt-dlp-tool-design.md`

---

## Phase Index

Execute phases in order. Each phase ends in a green test run + commit. Subsequent phases build on prior ones.

| Phase | File | Outcome |
|---|---|---|
| 1 | [phase-1-skeleton.md](2026-05-14-yt-dlp-tool-phase-1-skeleton.md) | Solution, projects, NativeAOT publish, packages locked, CI placeholder |
| 2 | [phase-2-domain.md](2026-05-14-yt-dlp-tool-phase-2-domain.md) | Models, `UrlValidator`, `FileNameSanitizer`, `TimeRangeValidator`, `ConfigStore`, `AppPaths`, `AppLogger` |
| 3 | [phase-3-security.md](2026-05-14-yt-dlp-tool-phase-3-security.md) | `Sha256Verifier`, `Ed25519Verifier`, `SigstoreVerifier` with test vectors |
| 4 | [phase-4-process.md](2026-05-14-yt-dlp-tool-phase-4-process.md) | `ProcessSandbox`, `YtDlpRunner`, `FfmpegRunner`, `ErrorMapper`; FakeYtDlp test helper |
| 5 | [phase-5-queue.md](2026-05-14-yt-dlp-tool-phase-5-queue.md) | `DownloadQueue`, `StateJournal` (JSONL persistence), crash recovery |
| 6 | [phase-6-update.md](2026-05-14-yt-dlp-tool-phase-6-update.md) | `UpdateManifest`, `UpdateChecker`, `UpdateApplier` (download → verify → atomic apply → rollback) |
| 7 | [phase-7-shell.md](2026-05-14-yt-dlp-tool-phase-7-shell.md) | App entry point, `MainWindow`, Mica/Acrylic interop, Aurora animation, theme resources, string resources |
| 8 | [phase-8-components.md](2026-05-14-yt-dlp-tool-phase-8-components.md) | `UrlInputView` (clipboard watch, debounce, metadata fetch), `FormatSelectorView`, `QualityDropdown`, `AdvancedOptionsView`, `SaveLocationView`, `QueuePanelView` + `QueueItemView` |
| 9 | [phase-9-settings.md](2026-05-14-yt-dlp-tool-phase-9-settings.md) | `UpdateBannerView`, `SettingsDialog`, toast notification wrapper |
| 10 | [phase-10-cicd.md](2026-05-14-yt-dlp-tool-phase-10-cicd.md) | `external-deps.json`, fetch script, PR check workflow, release workflow with Sigstore signing |

---

## Working agreements

- **TDD red-green-commit** for all Domain, Security, and Process layer code. UI code follows manual verification (run app, check behaviour) since WPF UI testing is brittle.
- **Commit after every green test** or every completed UI view. Small commits, conventional commit messages (`feat:`, `fix:`, `test:`, `refactor:`, `chore:`).
- **Never modify a previous phase's files unless explicitly directed.** If you find a bug in earlier phase code, raise it before fixing.
- **No reflection-heavy libraries.** Anything that breaks NativeAOT (e.g., Newtonsoft.Json, MahApps) is rejected.
- **NativeAOT smoke-test after every phase.** Run `dotnet publish -c Release -r win-x64 --self-contained true /p:PublishAot=true` from `src/YtDlpTool` and verify exit code 0. Failures here are usually reflection issues caught early.
- **All user-facing strings live in `Strings.zh-TW.xaml`.** Never hardcode Chinese strings in `.cs` or non-resource `.xaml` files.

---

## Repository prerequisites

Before Phase 1, ensure the working directory is a git repo:

```powershell
cd "C:\Users\Administrator\Desktop\yt-dlp tool"
git init
git branch -M main
```

Create `.gitignore` (root):

```
bin/
obj/
*.user
.vs/
.superpowers/
TestResults/
*.binlog
.update/
.fake-yt-dlp/
```

Commit:
```powershell
git add .gitignore docs/
git commit -m "chore: initial repo with spec and plan"
```

---

## Self-Review Checklist (run after all phases planned)

- [ ] Every spec section in `docs/superpowers/specs/2026-05-14-yt-dlp-tool-design.md` maps to at least one phase
- [ ] No `TBD` / `TODO` / `fill in details` placeholders in any phase file
- [ ] Method signatures referenced across phases match (e.g., `UrlValidator.Validate(string)` not `Validate(Uri)` in one place)
- [ ] Each phase has a clear "phase complete" gate (tests pass + commit + smoke test)

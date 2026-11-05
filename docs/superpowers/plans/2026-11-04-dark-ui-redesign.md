# Dark UI Redesign (v1.5.0) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the animated "Aurora Glass" UI with a flat, dark-only, single-screen UI that stays responsive under forced software rendering, without changing the download pipeline.

**Architecture:** One dark `Theme.xaml` (tokens + control templates, StaticResource only — no runtime theme switching). `MainWindow` uses the native title bar darkened via DWM, and three zones: a composer card (URL → video card → options → 下載), a queue header, and a virtualized queue list that owns its own scrolling. Logic that decides what the UI shows (quality list, status text, progress throttling) moves into pure classes in `YtDlpTool.Domain` so it is unit-tested.

**Tech Stack:** WPF on .NET 8 (`net8.0-windows10.0.19041.0`), CommunityToolkit.Mvvm 8.3.2 (existing), xUnit (existing). No new packages.

**Spec:** the user's requests in the 2026-11-04 session — "暗色介面、拿掉不必要的元素、精簡優化到最佳視覺與排版設計還有使用者體驗"; lag is "開啟緩慢" plus visible delay on dropdowns and the 下載數 slider; the target PCs have no Python, so everything ships bundled with minimal dependencies. Root cause of the lag (measured): the forever-running Aurora gradient Storyboard under DropShadowEffect cards, in software rendering forced by `KnownBadGpuRegex` — 95–100 % of a core idle with it, 0.0 % without.

## Global Constraints

- Dark only. `AppConfig.Theme` and `ThemePreference` stay for config compatibility but are ignored.
- Nothing animates while the app is idle. No `Effect` anywhere. No translucent brush larger than a control. Software rendering must stay cheap: `ApplyRenderMode` and `KnownBadGpuRegex` are NOT changed.
- No new NuGet packages, no new runtime dependencies, Windows 10 1809+ (`TargetPlatformMinVersion` 10.0.17763) keeps working.
- All UI copy Traditional Chinese. Version stays visible (title bar: `YtDlpTool v{version}`).
- `app.manifest` keeps `dpiAwareness=system`.
- Download pipeline, yt-dlp arguments and design commitments 1–8, 10–13 are untouched.
- Palette (exact): Bg `#0F1115`, Surface `#171A21`, SurfaceAlt `#1E222B`, SurfaceHover `#262B36`, SurfacePressed `#2E3440`, Border `#2A2F3A`, BorderStrong `#3A4150`, Text `#ECEEF2`, TextSecondary `#A3A9B5`, TextTertiary `#6E7582`, Accent `#D63A3F` (white text 4.6:1), AccentHover `#E5484D`, AccentPressed `#BF3036`, Success `#3DD68C`, Danger `#FF7A7A`, DangerSurface `#2A1618`, Warning `#F5B94A`, Focus `#9DB7FF`.
- Fonts: text `Microsoft JhengHei UI, Segoe UI`; icons `Segoe Fluent Icons, Segoe MDL2 Assets`.

## Review Focus

- A video whose formats include HLS entries, storyboards, missing sizes and several codecs per height → one quality row per height, no duplicates, no "?" noise (Task 1 tests).
- An audio-only or video-only source → the mode with no formats shows a disabled 畫質 list with 「此影片沒有可用的格式」, never a crash (Task 1 `Video_NoVideoFormats_Empty`, Task 5 view guard).
- Very long or CJK titles and long save paths → trimmed with ellipsis, full text in a tooltip, no horizontal overflow at the 780 px minimum width (Task 7 screenshot at min size).
- Many queue items, several downloading at once → progress updates throttled to ≤ 4/s per job and posted with `BeginInvoke`; the list virtualizes (Task 2 tests, Task 7 CPU check).
- Keyboard-only use → every control reachable with Tab, visible focus ring, Enter on the URL box parses, Esc cancels the selected job (Task 4 focus style, Task 7 check).

---

### Task 1: Quality list as a pure, tested builder

**Files:**
- Create: `src/YtDlpTool.Domain/Services/QualityOptions.cs`
- Modify: `src/YtDlpTool/Views/QualityDropdown.xaml.cs` (use the builder; delete `BuildOptions`/`Label*`/`FormatSize`)
- Test: `tests/YtDlpTool.Domain.Tests/Services/QualityOptionsTests.cs`

**Interfaces:**
- Produces: `public sealed record QualityChoice(string Label, VideoFormat Format) { public override string ToString() => Label; }` (ToString = what screen readers announce) and `public static class QualityOptions { IReadOnlyList<QualityChoice> Build(DownloadMode mode, IReadOnlyList<VideoFormat> formats); int DefaultIndex(IReadOnlyList<QualityChoice> choices); string FormatSize(long bytes); }`

Rules: video modes keep formats with `VideoCodec != null && Height > 0`, one choice per height, highest first; per height pick by codec rank `avc1` < `vp09`/`vp9` < `av01` < other, then larger `FileSizeBytes`. Label `"{h}p"` + `" (8K)"/" (4K)"/" (2K)"` for 4320/2160/1440 + `" · 約 {size}"` when the size is known, where AudioAndVideo adds the size of the best m4a audio. Audio mode: best m4a/mp4 audio → `"M4A · {abr} kbps · 約 {size}"`; best other audio → `"MP3（轉檔）"`; M4A first. `DefaultIndex`: first video choice with height ≤ 1080, else the last; audio 0. `FormatSize` uses 1024 steps and `0.#`: `"105 MB"`, `"1.2 GB"`.

- [ ] **Step 1: Write the failing tests** — `Video_OneChoicePerHeight_HighestFirst`, `Video_PrefersH264AtSameHeight`, `Video_AboveH264_PrefersVp9OverAv1`, `AudioAndVideo_LabelAddsM4aSize` (avc1 1080 100 MiB + m4a 5 MiB → `"1080p · 約 105 MB"`), `UnknownSize_OmitsSize` (`"720p"`), `Labels_Mark4KAnd2K`, `Video_IgnoresFormatsWithoutHeight`, `Video_NoVideoFormats_Empty`, `Audio_M4aFirstThenMp3`, `Audio_NoAudioFormats_Empty`, `DefaultIndex_PicksBestAtOrBelow1080`, `DefaultIndex_NothingAtOrBelow1080_PicksLast`, `Choice_ToStringIsLabel`.
- [ ] **Step 2: Run** `dotnet test tests/YtDlpTool.Domain.Tests -c Release --filter QualityOptions` → fails to compile (type missing).
- [ ] **Step 3: Implement** `QualityOptions` in the Domain file above.
- [ ] **Step 4: Run** the same command → all pass.

### Task 2: Queue status text and progress throttling

**Files:**
- Create: `src/YtDlpTool.Domain/Services/QueueStatusText.cs`, `src/YtDlpTool.Domain/Services/ProgressThrottle.cs`
- Test: `tests/YtDlpTool.Domain.Tests/Services/QueueStatusTextTests.cs`, `tests/YtDlpTool.Domain.Tests/Services/ProgressThrottleTests.cs`
- Modify: `src/YtDlpTool/ViewModels/QueueItemViewModel.cs`, `src/YtDlpTool/ViewModels/MainViewModel.cs`

**Interfaces:**
- Produces: `QueueStatusText.Format(JobStatus status, double percent, long? bytesPerSecond, TimeSpan? eta, string? failureReason) -> string` — Pending `"等待中"`; Downloading `"{percent:0}%"` + `" · {speed}"` + `" · 剩餘 {eta}"` (eta `m:ss`, or `h:mm:ss` from one hour); Completed `"已完成"`; Failed `failureReason ?? "下載失敗"`; Cancelled `"已取消"`. `QueueStatusText.FormatSpeed(long bps) -> string` (`"512 KB/s"`, `"3.2 MB/s"`). `ProgressThrottle(TimeSpan minInterval)` with `bool ShouldPublish(Guid jobId, double percent, DateTime nowUtc)` (true for the first report, for `percent >= 100`, or once `minInterval` has passed since the last published one) and `void Forget(Guid jobId)`.
- `QueueItemViewModel` gains `StatusText`, `TagText` (`"{QualityLabel} · {ModeLabel}"`), `IsActive` (Pending/Downloading), `IsDownloading`, `IsCompleted`, `IsFailed`, and `UpdateProgress(double percent, long? bps, TimeSpan? eta)`; `DisplayMeta` is removed.
- `MainViewModel.OnQueueEvent` posts with `Dispatcher.BeginInvoke` (never `Invoke`) and drops progress reports the throttle rejects (interval 250 ms); `Forget` on completion/failure/cancel.

- [ ] **Step 1: Write the failing tests** — status: `Pending`, `Downloading_FullLine` (`"62% · 3.2 MB/s · 剩餘 0:12"`), `Downloading_NoSpeedNoEta` (`"5%"`), `Downloading_LongEta` (`"1:02:03"`), `Completed`, `Failed_UsesReason`, `Failed_DefaultText`, `Cancelled`, `Speed_Units`. Throttle: `FirstReportPublishes`, `ReportsInsideIntervalAreDropped`, `ReportAfterIntervalPublishes`, `HundredPercentAlwaysPublishes`, `JobsAreIndependent`, `ForgetResets`.
- [ ] **Step 2: Run** `dotnet test tests/YtDlpTool.Domain.Tests -c Release --filter "QueueStatusText|ProgressThrottle"` → compile failure.
- [ ] **Step 3: Implement** both classes, then wire the view models as described.
- [ ] **Step 4: Run** the full Domain suite → all pass; `dotnet build` the app → 0 warnings.

### Task 3: Dark theme, dark title bar, remove the Aurora/glass layer

**Files:**
- Rewrite: `src/YtDlpTool/Resources/Theme.xaml`
- Create: `src/YtDlpTool/Interop/DarkTitleBar.cs`
- Delete: `Resources/Theme.Light.xaml`, `Resources/Theme.Dark.xaml`, `Services/ThemeService.cs`, `Views/AuroraBackground.xaml(.cs)`, `Interop/WindowChromeHelper.cs`, `Interop/WindowsVersion.cs`
- Modify: `App.xaml` (merge `Strings.zh-TW.xaml` + `Theme.xaml` only), `App.xaml.cs` (no ThemeService), `Views/Converters/StatusToColorConverter.cs` (keys below)

**Interfaces:**
- Produces resource keys (all `StaticResource`): brushes `Brush.Bg`, `Brush.Surface`, `Brush.SurfaceAlt`, `Brush.SurfaceHover`, `Brush.SurfacePressed`, `Brush.Border`, `Brush.BorderStrong`, `Brush.Text`, `Brush.TextSecondary`, `Brush.TextTertiary`, `Brush.Accent`, `Brush.AccentHover`, `Brush.AccentPressed`, `Brush.Success`, `Brush.Danger`, `Brush.DangerSurface`, `Brush.Warning`, `Brush.Focus` (frozen); fonts `Font.Default`, `Font.Icons`; styles `Card`, `Button.Primary`, `Button.Secondary`, `Button.Subtle`, `Button.Icon`, `Button.Link`, `RadioButton.Segment`, `Expander.Disclosure`, `ListBoxItem.Queue`, `Text.Title`, `Text.Caption`, plus implicit dark styles for `TextBox`, `ComboBox`/`ComboBoxItem` (dark popup), `CheckBox`, `Slider`, `ScrollBar` (thin, no arrows), `ProgressBar` (flat; indeterminate animation only while `IsIndeterminate`), `ToolTip`, and a `FocusVisualStyle` using `Brush.Focus`.
- `DarkTitleBar.Apply(Window w)`: DWM attribute 20 (19 on builds < 18985) = 1; on build ≥ 22000 also caption color 35 = Bg and border color 34 = Border. Best-effort, never throws.

- [ ] **Step 1:** Write `Theme.xaml` and `DarkTitleBar`; delete the files above; fix every reference (`Brush.TextPrimary` → `Brush.Text`, `GlassCard` → `Card`, `PrimaryButton` → `Button.Primary`, `SecondaryButton` → `Button.Secondary`, `LinkButton` → `Button.Link`, `IconButton` → `Button.Icon`, `DangerIconButton` → `Button.Icon`).
- [ ] **Step 2:** `dotnet build` → 0 warnings, 0 errors; launch the app → no XAML parse error in `%TEMP%\YtDlpTool-startup-*.log`.

### Task 4: Main window and composer layout

**Files:**
- Modify: `MainWindow.xaml(.cs)`, `Views/UrlInputView.xaml(.cs)`, `Views/FormatSelectorView.xaml(.cs)`, `Views/QualityDropdown.xaml(.cs)`, `Views/AdvancedOptionsView.xaml(.cs)`, `Views/SaveLocationView.xaml(.cs)`, `Views/HealthBannerView.xaml`, `Views/UpdateBannerView.xaml`, `Resources/Strings.zh-TW.xaml`
- Create: `Views/VideoCardView.xaml(.cs)`

**Interfaces:**
- `MainViewModel` gains `ImageSource? CurrentThumbnail`, `bool HasMetadata`, `string MetaSubtitle` (`"{Channel} · {duration}"`, duration `m:ss`/`h:mm:ss`).
- Window: native title bar, 1000×720, min 780×560, `Background=Brush.Bg`, 20 px margin. Rows: banners (Auto) / composer `Card` (Auto) / queue header (Auto) / queue list (*).
- Composer: URL box (link glyph, placeholder 「貼上 YouTube 網址，或按 Ctrl+V」, clear ✕ inside when not empty) + 貼上 (`Button.Secondary`) + 開啟下載資料夾 and 設定 (`Button.Icon`, tooltips). Under it while parsing: 2 px indeterminate bar + 「正在讀取影片資訊…」 (+ the existing warm-up hint); on failure the error in `Brush.Danger` with ⚠ glyph, wrapping. After a successful parse a divider and `VideoCardView` (thumbnail 192×108 radius 6, title up to 2 lines, MetaSubtitle), then one options row: 格式 segmented `影片 | 音訊 | 無聲影片` (AudioAndVideo | AudioOnly | VideoOnly), 畫質/音質 combo (240 px), spacer, `下載` primary button (download glyph, 40 px, min 120). Last row: 更多選項 disclosure (subtitles as check chips, max 3; clip range) on the left, 儲存到 {path} 變更 on the right. Remove: logo ellipse, custom min/max/close, FirstHint, emoji labels, the stand-alone cards.
- After 下載: URL box gets focus with its text selected, so the next Ctrl+V replaces it.

- [ ] **Step 1:** Build the layout; keep every existing behaviour (debounce, Enter to parse, warm-up wait, SSL prompt, Ctrl+V, Esc).
- [ ] **Step 2:** `dotnet build` → 0 warnings; launch; parse a real URL through UI Automation; screenshot empty / parsing / parsed / error states.

### Task 5: Queue list

**Files:** Modify `Views/QueuePanelView.xaml(.cs)`, `ViewModels/MainViewModel.cs`

**Interfaces:**
- `MainViewModel` gains `OpenItemFolderCommand(Guid id)` (`explorer.exe /select,"<OutputFilePath>"`, falls back to the folder) and `bool HasTerminalItems` (drives 清除已完成's IsEnabled).
- Header: 「下載清單」 + count, 清除已完成 (`Button.Link`) on the right. List: `ListBox` with `VirtualizingPanel.IsVirtualizing=True`, `VirtualizationMode=Recycling`, `ScrollUnit=Pixel`, inside its own Grid row (never inside an outer ScrollViewer). Row: thumbnail 112×63 radius 4 · title (1 line, ellipsis, tooltip) · `StatusText` (Danger + ⚠ when failed, Success + ✓ when completed) · `TagText` · 4 px progress bar only while `IsDownloading` · actions: 在資料夾中顯示 (completed) and ✕ (cancel/remove, tooltip 取消/移除). Empty state: download glyph + 「還沒有下載項目」 + 「貼上 YouTube 網址後按「下載」」.

- [ ] **Step 1:** Implement; **Step 2:** enqueue a real download via UI Automation; screenshot downloading / completed / failed rows.

### Task 6: Dialogs

**Files:**
- Create: `Dialogs/AppDialog.xaml(.cs)` — `public static MessageBoxResult Show(Window? owner, string message, string title, MessageBoxButton buttons = MessageBoxButton.OK, MessageBoxImage icon = MessageBoxImage.None)`; supports OK, OKCancel, YesNo, YesNoCancel; glyph + color per icon; Esc = Cancel/No; Enter = default (OK/Yes); scrolls long text; owner defaults to the active window.
- Modify: every `MessageBox.Show` call (App.xaml.cs ×5, MainWindow ×3, SettingsDialog ×3, HealthBannerView ×1, UpdateBannerView ×2) → `AppDialog.Show`.
- Modify: `Dialogs/SettingsDialog.xaml(.cs)` — dark, native dark title bar, sections 下載 (預設儲存資料夾, 同時下載數量 slider 1–10 with value), 更新 (檢查頻率, 立即檢查並更新 + status/progress/診斷), 進階 (開啟紀錄資料夾, 重新下載元件, 檢視根憑證指紋, 關於, 允許不受信任憑證 + warning). Removed: 語言 (one option), 主題 (dark only), the two per-component update checkboxes (they gate nothing — see 04-update-flow.md), 開啟下載資料夾 (duplicates the main window), the Aurora layer. Save leaves `YtDlpCheckFrequency`/`FfmpegCheckFrequency`/`Theme` unchanged.
- Modify: `Dialogs/FilenameConflictDialog.xaml` — dark, buttons 取消 (Secondary) / 覆蓋 (Secondary) / 自動改名 (Primary, default).

- [ ] **Step 1:** Implement; **Step 2:** `grep -rn "MessageBox.Show" src/YtDlpTool --include=*.cs` → only inside `AppDialog`; open Settings and each dialog once and screenshot.

### Task 7: End-to-end verification

- [ ] `dotnet build YtDlpTool.sln -c Release` → 0 warnings; `dotnet test` → all pass.
- [ ] Fresh launch on the software-rendered dev box: idle CPU over 10 s < 2 % of one core (old UI: 95–100 %).
- [ ] UI Automation: paste → parse → pick 360p → 下載 → file exists and `ffprobe` shows h264 + aac.
- [ ] CPU while one download runs: report the number.
- [ ] Startup ("開啟緩慢"): time from launch to first visible window, v1.4.0 vs the new single-file publish; then try `EnableCompressionInSingleFile=false` and `PublishReadyToRun=true` and keep whichever measurably helps (report exe size vs. seconds saved).
- [ ] Screenshots at 1000×720 and at the 780×560 minimum; check nothing clips.
- [ ] Keyboard pass: Tab order, focus ring visible, Enter parses, Esc cancels.

### Task 8: Docs

- [ ] `02-design-commitments.md` #9: dark-only theme, StaticResource; reintroducing runtime theme switching means going back to DynamicResource.
- [ ] `05-debugging-playbook.md` "UI lag" and `07-version-history.md` v1.5.0: replace the forward-looking lines with what shipped and the measured numbers.
- [ ] `README.md`: "Aurora Glass UI" → dark UI; verify section names `yt-dlp-pkg.zip`.

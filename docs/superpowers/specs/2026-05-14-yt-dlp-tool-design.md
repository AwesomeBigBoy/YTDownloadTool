# YtDlpTool · 輕量化 YouTube 下載工具 · 設計規格

**Status**: Design approved, ready for implementation planning
**Date**: 2026-05-14
**Target**: v1.0.0

---

## 1. 概覽

一款 Windows 桌面 YouTube 下載工具，以 `yt-dlp` 為核心引擎，提供現代化「Aurora Glass」風格的繁體中文介面。設計目標是**極簡操作 + 嚴謹安全**：使用者貼上網址、選格式、選位置、按下載；底層完成輸入驗證、子行程隔離、簽章驗證、原子更新等防護。發佈為可攜資料夾，無需安裝、無需 admin、AD 管控環境可用。

### 設計優先順序
1. **安全**：使用者輸入驗證、子行程隔離、更新雙重簽章驗證 — 不協商
2. **簡單**：UI 不暴露技術細節，預設值合理，無 onboarding
3. **輕量**：app 本體 ~50–80 MB（self-contained single-file，含壓縮），打包後總體 ~100–130 MB（含 yt-dlp.exe + ffmpeg.exe essentials build）。**原規劃 ~15 MB 用的是 NativeAOT，但 Phase 1 實作時發現 WPF 與 NativeAOT 在 .NET 8 不相容（SDK NETSDK1168），改用 self-contained 單檔。AD 信任度與其他承諾不變。**
4. **可攜**：portable zip，解壓即用，所有狀態寫到 `%LOCALAPPDATA%`

### 非目標（v1 不做）
- 跨平台（僅 Windows 10 1809+ / Windows 11）
- 播放清單批次下載、私人影片登入、檔名範本、速度限制
- 遙測、雲端同步、下載歷史
- Authenticode 簽章（v2 再加 Azure Trusted Signing）

---

## 2. 技術選型與取捨

| 決策 | 採用 | 替代方案 | 取捨理由 |
|---|---|---|---|
| 語言/框架 | C# .NET 8 + WPF + 自包含單檔 | Tauri (Rust+Web) / Wails (Go+Web) | AD 環境信任度最高；WPF 不依賴 WebView2 runtime，相容老 Win10/LTSC。**NativeAOT 在 .NET 8 與 WPF 不相容，已改採 self-contained single-file publish（~50-80 MB）。** |
| 發佈型態 | Portable zip (folder layout) | 單一 exe 內嵌資源 | 啟動瞬開、yt-dlp 獨立更新方便、AD 部署友善 |
| 更新簽章 | Ed25519 via Sigstore keyless | 軟體金鑰 / YubiKey | 0 元、零金鑰管理、用 GitHub OIDC 自動簽 |
| Code Signing | v1 不做 | Azure Trusted Signing (~NT$330/月) | v1 接受 SmartScreen 警告，README 提供解封鎖說明；v2 升級不影響既有安裝 |
| 更新來源 | GitHub Releases API | 自架 endpoint | AD 通常允許 github.com；簽章驗證已防止 release 被竄改 |
| ffmpeg | bundle essentials build | 首次啟動下載 | AD 環境可能擋；體積換確定性 |
| UI thread | WPF Dispatcher + IProgress<T> | 完全 async/await | WPF 慣例；NativeAOT 相容 |

### 視覺風格取捨：Aurora Glass × WPF
使用者選定的 Aurora Glass 視覺以毛玻璃 + 極光漸層為核心。WPF 實作方式：
- **視窗背景**：Win11 使用 Mica（`WindowChromeHelper` 呼叫 `DwmSetWindowAttribute` 設 `DWMSBT_MAINWINDOW`），Win10 1809+ 使用 Acrylic（`SetWindowCompositionAttribute` API），Win10 1803 及更早 fallback 為扁平半透明漸層。
- **Aurora 動畫層**：在視窗背景之上疊 `Border` 含 `LinearGradientBrush`，三色節點 `#A8C4E8 / #D4B3D8 / #F5C9B3`，opacity 0.35；`DoubleAnimation` 對 `GradientStop.Offset` 做 30 秒緩慢循環，UI 有「呼吸感」但不干擾。
- **面板毛玻璃**：每個玻璃卡片用半透明背景 + `BlurEffect`（輕量、效能可接受）。

---

## 3. 系統架構

### 3.1 三層結構

```
┌─────────────────────────────────────────────┐
│ UI Layer (WPF / XAML / MVVM)               │
│  - Views, ViewModels                       │
│  - 純呈現，不接觸 yt-dlp / ffmpeg          │
└─────────────────────────────────────────────┘
                    ↕ ViewModel ↔ Service
┌─────────────────────────────────────────────┐
│ Domain Layer (純 C#)                       │
│  - Models: VideoMetadata, DownloadJob      │
│  - Services: UrlValidator, DownloadQueue,  │
│    UpdateChecker, UpdateApplier,           │
│    ConfigStore, ErrorMapper                │
│  - Security: Ed25519Verifier, Sha256Verifier│
│  - 可單元測試，不依賴 WPF                  │
└─────────────────────────────────────────────┘
                    ↕ Process invocation
┌─────────────────────────────────────────────┐
│ Process Layer                              │
│  - YtDlpRunner, FfmpegRunner               │
│  - ProcessSandbox (env whitelist, timeout) │
│  - 隔離所有對外部 binary 的呼叫            │
└─────────────────────────────────────────────┘
                    ↓
            yt-dlp.exe / ffmpeg.exe (子行程)
```

### 3.2 Portable Folder Layout

```
YtDlpTool\                       (使用者解壓位置或 %LOCALAPPDATA%\YtDlpTool)
  YtDlpTool.exe                  ~15 MB · NativeAOT 自包含
  bin\
    yt-dlp.exe                   ~17 MB · 鎖版本，CI 驗 SHA-256
    ffmpeg.exe                   ~30 MB · essentials build (gpl)
  resources\
    update-pubkey-fingerprint.txt  Sigstore 簽署者身份字串，使用者可參照驗證
  config.json                    使用者偏好；首次啟動建立
  state.log                      下載佇列狀態（JSONL，崩潰恢復用）
  logs\                          rolling 7 days
  .update\                       更新流程暫存區
```

當 app 從唯讀位置（如 USB 隨身碟）啟動，狀態與日誌一律改寫到 `%LOCALAPPDATA%\YtDlpTool\` 影子目錄；app 啟動時偵測寫入權限決定。

### 3.3 元件清單

#### UI 層（WPF）
- `MainWindow.xaml` — 主視窗殼，Mica/Acrylic 背景，Aurora 動畫層
- `Views/UrlInputView` — 網址輸入、剪貼簿偵測（不主動填）、解析中 spinner、VideoMetaCard
- `Views/FormatSelectorView` — Segmented control 三模式（音訊/影音/影像）
- `Views/QualityDropdown` — 自訂 popup，顯示解析度與檔案大小估算
- `Views/AdvancedOptionsView` — 摺疊：字幕多選、片段切割輸入
- `Views/SaveLocationView` — 路徑顯示 + 瀏覽（`Microsoft.Win32.OpenFolderDialog`，.NET 8 WPF-native，AOT 相容）
- `Views/QueuePanelView` — 摺疊佇列；每列 `QueueItemView`
- `Views/UpdateBannerView` — 頂部滑入橫幅
- `Dialogs/SettingsDialog` — 並行數、預設儲存位置、更新頻率、語言、主題、關於
- `Resources/Strings.zh-TW.xaml` — 所有面向使用者字串

#### Domain 層
- `Models/VideoMetadata` — 標題、頻道、時長、格式清單、字幕清單、縮圖
- `Models/DownloadJob` — id、URL、模式、品質、字幕、切片、儲存路徑、狀態、進度
- `Services/UrlValidator` — 主機白名單、canonical 化、防 SSRF
- `Services/DownloadQueue` — 並行控制（1-5，預設 2）、新增/取消、事件廣播、狀態持久化
- `Services/UpdateChecker` — 三軌（yt-dlp 7d / ffmpeg 30d / app 30d）
- `Services/UpdateApplier` — 下載、驗章、驗 hash、原子改名、rollback
- `Services/ConfigStore` — System.Text.Json source generator (AOT 相容)
- `Services/ErrorMapper` — yt-dlp/ffmpeg 錯誤訊息 → 中文友善訊息

#### Security
- `Security/Ed25519Verifier` — NSec.Cryptography (AOT 友善 libsodium binding)
- `Security/Sha256Verifier` — .NET 內建
- `Security/SigstoreVerifier` — 解析 sigstore bundle、驗證 Fulcio cert 鏈、Rekor log entry、預期身份字串

#### Process 層
- `Process/YtDlpRunner` — `ProcessStartInfo.ArgumentList`、`--newline --progress-template`、CancellationToken→Kill
- `Process/FfmpegRunner` — 單獨切片場景（多數時候 yt-dlp 自呼）
- `Process/ProcessSandbox` — env whitelist、stdout buffer 上限、超時、CreateNoWindow、強制 UTF-8

#### 跨領域
- `Telemetry` — **預設關閉**，僅本地 logs
- 日誌：等級 Error/Warn/Info/Debug，rolling 7 天，**不寫 URL 與標題全文**

---

## 4. 資料流

### 4.1 解析網址 → 顯示格式

```
使用者貼上 URL
  → UrlInputView.OnTextChanged (300ms debounce)
  → UrlValidator.IsValidYouTubeUrl(text)
  → YtDlpRunner: --dump-single-json --no-playlist <url>  (僅讀，不下載)
  → 解析 JSON → VideoMetadata（過濾格式：每模式 2-3 品質）
  → FormatSelectorView 顯示
```

`--no-playlist` 強制單一影片，即使 URL 含 `&list=`。

### 4.2 加入下載 → 完成

```
使用者按「加入下載」
  → 組 DownloadJob → DownloadQueue.Enqueue
  → 並行控制器排程（預設 2 並行）
  → YtDlpRunner 啟動，引數陣列：
      -f <format>
      --output "<sanitized-path>/<sanitized-name>"
      [--write-subs --sub-langs <list> --embed-subs]
      [--download-sections "*HH:MM:SS-HH:MM:SS" --force-keyframes-at-cuts]
      [--embed-thumbnail]
      --newline --progress-template "<json>"
  → 逐行讀 stdout → ProgressUpdated 事件 → UI 更新
  → 完成 → Windows toast (CommunityToolkit.WinUI.Notifications)
```

### 4.3 更新檢查 + 一鍵更新

```
啟動後 60 秒（避免拖慢冷啟）→ UpdateChecker timer:
  HTTPS GET api.github.com/repos/<owner>/<repo>/releases/latest
  下載 manifest.json + manifest.json.sigstore
  SigstoreVerifier.Verify(manifest, bundle, expectedIdentity) → 否則中止
  解析版本 → 比對本地 → 有新版 → UpdateBannerView 滑入

使用者點「一鍵更新」→ UpdateApplier:
  1. 下載 <file>.new 到 .update/
  2. SHA-256(file) == manifest.files[file].sha256 → 否則中止+刪
  3. Sigstore 驗證 <file>.sigstore → 否則中止+刪
  4. 暫停所有使用該檔的下載任務
  5. rename current → .old; rename .new → current
  6. 呼叫 --version 驗證可執行
  7. 成功 → 刪 .old；失敗 → 反向 rename + 通知
  8. 恢復佇列
```

**所有更新操作在 `%LOCALAPPDATA%\YtDlpTool\` 內，不需 admin。**

---

## 5. 安全模型

### 5.1 輸入驗證

| 輸入 | 防禦 |
|---|---|
| YouTube URL | 主機白名單 `^(www\.)?(youtube\.com\|youtu\.be)$`；解析後 reconstruct canonical 形式；只接受 https；拒絕 IP literal、URL-encoded host、IDN homograph |
| 儲存路徑 | `Path.GetFullPath()` 後驗證在使用者選定根目錄之下；拒絕指向系統的 reparse points / symlinks；拒絕保留檔名（CON、PRN、AUX…） |
| 自動生成檔名 | `SanitizeFileName`：移除 `< > : " / \| ? *`、控制字元、trailing dots/spaces、Unicode 方向覆寫 (U+202E)；截斷 200 字元；空 fallback `video_<timestamp>` |
| 切片時間 | regex `^\d{1,2}:\d{2}:\d{2}$`；start < end ≤ duration；最長 8 小時 |
| 字幕語言代碼 | 從 yt-dlp 回傳清單中挑選，不接受手動輸入 |

### 5.2 子行程呼叫

- **永不拼 shell 字串**。`ProcessStartInfo.ArgumentList`（每個引數獨立 escape）
- `UseShellExecute = false`、`CreateNoWindow = true`
- **環境變數白名單**：子行程只繼承 `SystemRoot`、`Temp`、`Path`；`Path` 重組為 `<app>\bin;<system32>`，不繼承使用者 PATH（防 PATH 劫持）
- **超時**：metadata 解析 30 秒；下載任務無上限但 CancellationToken 可取消（先 `CloseMainWindow`，800ms 寬限後 `Kill(entireProcessTree: true)`）
- **stdout buffer 上限**：每子行程 10 MB，防 stdout 灌爆

### 5.3 更新流程簽章

**雙重簽章策略**：
1. `manifest.json.sigstore` 驗證整份清單由預期身份簽署
2. 每個檔案的 `<file>.sigstore` 獨立驗證，**即使 manifest 被竄改、攻擊者也無法為新檔產生有效簽章**

**Sigstore keyless 細節**：
- 簽署端：GitHub Actions workflow 在 push tag 時以內建 OIDC token 向 Fulcio 換短期憑證（~10 分鐘有效），簽完即丟
- 驗證端：app 內嵌
  - Sigstore 根 CA 公鑰（業界常數）
  - Rekor 透明日誌公鑰
  - 預期簽署者身份字串：`https://github.com/<owner>/<repo>/.github/workflows/release.yml@refs/tags/v*`
- 驗證鏈：cert 鏈到 Fulcio root → cert 內 OIDC subject == 預期身份 → Rekor 包含證明 → 簽章時憑證有效

**為什麼這夠安全**：
- 短期憑證過期後攻擊者拿不回去簽
- 簽署紀錄寫入公開透明日誌 (Rekor)，任何人可審計
- 攻擊者偷到 GitHub session 也只能在登入期間濫用，跟長期金鑰被偷可永久濫用完全不同

### 5.4 程式碼安全
- **NativeAOT** 預設啟用 DEP/ASLR/CFG mitigations，不關
- **不引入 reflection-heavy 套件** — 縮小攻擊面，也是 AOT 必要條件
- **依賴清單**（全部官方或微軟同源）：
  - `System.Text.Json`（內建，source generator）
  - `CommunityToolkit.Mvvm`
  - `CommunityToolkit.WinUI.Notifications`
  - `NSec.Cryptography`（Ed25519，libsodium binding）
  - 完。無 Newtonsoft / Serilog / MahApps / WindowsAPICodePack

### 5.5 隱私
- **離線優先**：啟動不打網路；只有兩種情境連網：(1) 解析/下載影片 (2) 更新檢查（可關閉）
- **無遙測**
- `config.json` 不記錄網址或下載歷史；只記偏好（資料夾、語言、並行數、上次更新檢查時間）
- 日誌**不寫 URL/標題全文**（只記類別、錯誤代碼、耗時、hash 後綴）

### 5.6 AD 環境相容
- 全程在 `%LOCALAPPDATA%`，**不需 admin**
- 不寫註冊表（除 Windows toast AppUserModelID 必要項）
- 不裝服務、不開 inbound port、不改 hosts
- 只 outbound HTTPS 443
- v1 接受 SmartScreen 警告，提供使用者解封鎖說明；v2 加 Azure Trusted Signing

---

## 6. UI/UX 規格

### 6.1 視覺基礎
- **字體**：UI 內文 Microsoft JhengHei UI（fallback Segoe UI Variable）；數字用 Segoe UI tabular-nums
- **窗體**：1280×800 預設，最小 900×600，圓角 12px
- **背景**：Win11 Mica / Win10 1809+ Acrylic / 1803- 扁平 fallback；之上 Aurora 漸層層
- **色彩語意**：
  - 主操作：深炭 `#2A2A3E`，hover `#3D3D55`
  - 危險：暖紅 `#C45D5D`
  - 成功：薄荷 `#7EB89B`
  - 文字主 `rgba(42,42,62,0.92)`、次 `0.55`、淡 `0.35`
  - 深色模式：背景 `#1A1A24`，漸層飽和度 -40%
- **間距系統**：4 / 8 / 12 / 16 / 24 / 32 / 48
- **動效**：200ms cubic-bezier(.4,0,.2,1)；hover 微上抬 + 陰影加深

### 6.2 主畫面元件

**頂部欄**（56px）：app 名 + logo · 設定齒輪 · 視窗按鈕

**URL 輸入區**（玻璃卡，padding 20，圓角 14）
- Placeholder「貼上 YouTube 網址…」
- 剪貼簿偵測：app 取得焦點時若剪貼簿是 YouTube URL，**右側出現「貼上」按鈕，不主動填**
- 300ms debounce → 解析中 spinner
- 完成後滑入 VideoMetaCard：縮圖 96×54 + 標題/頻道/時長 + 清除

**模式選擇器**（Segmented control）
- 🎵 純音訊 / 🎬 影音合併 / 🎥 純影像
- 切換 200ms 淡入淡出

**品質下拉**（自訂玻璃 popup）
- 影音/影像：依實際可用解析度（1080p / 720p / 480p…），每列「1080p · ~120 MB · H.264」（檔案大小估算來自 yt-dlp metadata）
- 純音訊：高音質 M4A 256k / 標準 M4A 128k / MP3 320k（轉碼）/ MP3 192k（轉碼）

**進階選項**（摺疊，預設關閉）
- 字幕：勾選方塊列出可用語言，自動生成標 `auto`，最多選 3 個，嵌入為 soft sub
- 片段切割：開關 + 兩格 `00:00:00` mask input + 預覽長度

**儲存位置**：單行路徑 + 「瀏覽…」連結；tooltip 顯示完整路徑

**主按鈕**（滿格 48px 圓角 12）
- 無 URL 時 disabled，文字「請先貼上網址」
- 點下 → 300ms 收縮 → 變綠色「已加入佇列」 → 1.5s 後恢復；URL 區自動清空

### 6.3 下載佇列面板
- 有任務自動展開
- 每項目：縮圖 64×36 + 標題 + 進度條 4px + metadata `65% · 12.4 MB/s · 剩餘 00:01:23 · 720p 影音` + 取消叉叉
- 狀態色條（3px 高）：等待灰 / 下載中深炭 / 完成薄荷 / 失敗暖紅
- 失敗：metadata 變「下載失敗：影片有地區限制」+ 「重試」「複製錯誤」

### 6.4 更新橫幅
- 滑入 56px。三軌（yt-dlp / ffmpeg / app）獨立追蹤，但同一時間若多軌都有新版，合併為一則橫幅：
  - 單軌：「**有新版本可更新** · yt-dlp 2026.05.14」
  - 多軌：「**有 N 個元件可更新** · yt-dlp、ffmpeg」（點開展開詳情）
- 「一鍵更新」 + 「稍後」
- 點下：橫幅變進度條「下載中 32%」→「驗證中…」→「套用中…」→「✓ 已更新」→ 2 秒收起
- 失敗：紅「更新失敗，已自動還原。點此查看詳情」

### 6.5 設定對話框（modal，背景模糊）
- **下載**：預設儲存資料夾、並行下載數 slider 1-5
- **更新**：檢查頻率（每次啟動 / 每天 / 每週 / 不檢查）、yt-dlp 與 ffmpeg 個別開關
- **介面**：語言（繁中啟用，其他預留）、主題（跟隨系統 / 亮色 / 深色）
- **進階**：開啟日誌資料夾、開啟下載目錄、版本資訊、關於（Sigstore 身份字串、開源授權清單）

### 6.6 首次啟動 + 無障礙
- **無 onboarding**。第一次貼上 URL 時下方淡淡顯示「品質下拉可選不同解析度 · 右下可開進階選項」3 秒淡出
- 鍵盤：`Ctrl+V` 在任何位置貼 URL；`Enter` 觸發加入下載；`Esc` 取消當前選中佇列；Tab 順序明確；focus ring 2px

---

## 7. 錯誤處理

**永遠不暴露 yt-dlp/ffmpeg 原始 stderr。** 由 `ErrorMapper` 分類翻譯。

### 7.1 分類

**A. 使用者輸入錯誤**（非阻斷紅字提示）
- 非 YouTube 網址 / 切片格式錯 / start ≥ end / 超過影片長度 / 儲存路徑無寫入權限

**B. 網路/YouTube 錯誤**（佇列項目失敗 + 重試連結）
- 403 / Sign in to confirm → 「YouTube 拒絕了這次請求，影片可能有年齡或地區限制」
- 429 → 「YouTube 暫時限速」， **自動延遲 30 秒重試一次**
- 連線中斷 → 「網路連線中斷，請重試」
- Video unavailable / private → 「無法下載（可能已刪除/設為私人/下架）」
- Premiere → 「預定首播影片，請首播後再下載」

**C. 系統/處理錯誤**
- 磁碟空間不足 → 暫停佇列 + 橫幅
- 檔名衝突 → 對話框「覆蓋 / 改名為 `<name>_2.mp4` / 取消」，預設改名
- ffmpeg/yt-dlp 缺失損毀 → 提示「修復元件」（觸發更新流程重抓）

**D. 未預期錯誤**
- 「下載失敗（錯誤代碼 E-${hex6}）」 + 「複製技術細節」 + 寫入 logs

### 7.2 取消
- CancellationToken → `Process.CloseMainWindow()` → 800ms → `Process.Kill(entireProcessTree: true)`
- 清理 `.part`、`.ytdl` 暫存
- 佇列項目移除（不留歷史）

### 7.3 崩潰防禦
- 全域 unhandled exception 接管（Dispatcher / AppDomain / TaskScheduler）
- 崩潰寫 crash log → 對話框 → 退出
- **下載狀態持久化**：DownloadQueue 每次狀態變更寫 JSONL 到 `state.log`；重啟時讀回顯示「上次有 N 個下載未完成，要恢復嗎？」

### 7.4 日誌
- 等級 Error/Warn/Info/Debug，預設 Info；設定可調 Debug
- Rolling 每日一檔，保留 7 天
- **不寫**：URL 全文、影片標題、檔案路徑全名（hash 後綴代替）
- 寫入：操作類別、錯誤代碼、耗時

---

## 8. 測試策略

### 8.1 單元測試（xUnit，Domain 層覆蓋 80%+）
- `UrlValidator` — 合法變體 + 攻擊向量（`file://`, `javascript:`, IDN homograph、URL-encoded host）
- `SanitizeFileName` — `..\..\x`, `CON`, 控制字元、U+202E
- `Ed25519Verifier` — rfc8032 test vectors + 失敗案例
- `Sha256Verifier` — NIST 向量
- `ErrorMapper` — 每個已知 yt-dlp 錯誤特徵都有對應映射
- `DownloadQueue` — 並行控制、取消、狀態轉移
- `TimeRangeValidator` — 邊界值

### 8.2 整合測試（fake yt-dlp.exe）
- 完整下載流程：metadata → 排程 → 進度 → 完成
- 更新流程（本地 HTTP server 模擬 GitHub）：簽章/雜湊驗證、原子改名、rollback
- 取消流程：確認子行程結束 + 暫存清

### 8.3 端對端（手動 checklist，每次釋出）
- 三模式 × 兩品質下載真實短影片
- 字幕嵌入、片段切割、縮圖嵌入
- 故意斷網
- Win10 1809 + Win11 雙 VM 跑一次

### 8.4 安全測試（大版更新前）
- 篡改 manifest / 篡改檔案 → 確認被拒絕
- 模擬磁碟滿、檔案被佔用、權限拒絕 → graceful 失敗

---

## 9. 建置與發佈管線

### 9.1 GitHub Actions

**PR check**：
```
dotnet restore (locked mode)
dotnet build -c Release
dotnet test
dotnet publish src/YtDlpTool/YtDlpTool.csproj -c Release -r win-x64
驗證 AOT 編譯成功
```

**Release (push tag v*)**：
```
1. 跑完 PR check
2. 從 yt-dlp 與 ffmpeg 官方 release 抓 pinned 版本，驗 SHA-256
3. 組 portable folder
4. 計算每個檔案 SHA-256
5. 產 manifest.json
6. sigstore-action 簽 manifest.json → manifest.json.sigstore
7. sigstore-action 對個別檔案逐個簽 (雙保險)
8. 壓 YtDlpTool-v<ver>-win-x64.zip
9. gh release create 上傳
```

**單一手動操作**：`git tag v1.0.0 && git push --tags`

### 9.2 依賴鎖定
- yt-dlp 與 ffmpeg 版本 + SHA-256 寫死於 `build/external-deps.json`
- NuGet 套件鎖 `packages.lock.json`，`RestoreLockedMode=true`
- Dependabot **僅自動 PR NuGet 套件**；外部 binary 升級手動評估

### 9.3 版本節奏
- 語意化版號 `MAJOR.MINOR.PATCH`
- yt-dlp/ffmpeg 升級獨立 release，app 版號不動，檔名後綴標 yt-dlp 版本
- Bug fix 隨修隨出；feature 一兩個月

---

## 10. 開放問題 / 未來工作

1. **Azure Trusted Signing**（v2）：預算允許時加，免除 SmartScreen 警告
2. **金鑰輪替**：若需從 Sigstore 切到自管金鑰，app 內可同時嵌入兩把公鑰過渡
3. **語系擴充**：UI 已用 XAML resource 集中字串，加英文/簡中只需多一份檔案
4. **支援其他影音站**：yt-dlp 本身支援，需擴充 `UrlValidator` 白名單；UI 文案需調整為非 YouTube-specific
5. **下載歷史**：v1 主動避免；若使用者要求可後加，需明確 opt-in + 加密儲存

---

## 附錄 A · 預設值總表

| 項目 | 預設值 | 可調 |
|---|---|---|
| 並行下載數 | 2 | 1-5 |
| 預設儲存資料夾 | `%USERPROFILE%\Downloads\YtDlpTool` | 任意 |
| 字幕嵌入 | 是（影音模式） | 否 |
| 縮圖嵌入 | 是 | 否 |
| 切片精度 | 精準（重編碼） | — |
| yt-dlp 檢查頻率 | 7 天 | 每次啟動/7/30/不檢查 |
| ffmpeg 檢查頻率 | 30 天 | 同上 |
| App 檢查頻率 | 30 天 | 同上 |
| 語言 | 繁體中文 | 簡中、英文（未來） |
| 主題 | 跟隨系統 | 亮色 / 深色 |
| 日誌等級 | Info | Debug |
| 日誌保留 | 7 天 | — |

## 附錄 B · 依賴版本鎖定示範

格式範例 — 實際 hash 由首次 CI 跑出後填入並 commit；後續升級時 GitHub Actions 跟此檔案的 hash 驗證下載檔，不符就中止建置。

```json
// build/external-deps.json
{
  "yt-dlp": {
    "version": "2026.05.01",
    "url": "https://github.com/yt-dlp/yt-dlp/releases/download/2026.05.01/yt-dlp.exe",
    "sha256": "TO_BE_FILLED_AT_FIRST_BUILD"
  },
  "ffmpeg": {
    "version": "7.1-essentials",
    "url": "https://www.gyan.dev/ffmpeg/builds/ffmpeg-7.1-essentials_build.zip",
    "sha256": "TO_BE_FILLED_AT_FIRST_BUILD"
  }
}
```

## 附錄 C · 待 CI 設定時補上

- GitHub repo owner/name → 寫入 `SigstoreVerifier` 預期身份字串常數
- Workflow 路徑名（如 `.github/workflows/release.yml`）→ 同上
- 第一次釋出後驗證 Sigstore bundle 能在 app 內成功驗證（端對端煙霧測試）

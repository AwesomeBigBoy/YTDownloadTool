# Phase 7 · WPF Shell · Mica/Acrylic · Aurora · Theme · Strings

**Goal:** Replace the placeholder MainWindow with the real shell — Mica/Acrylic background with version-aware fallback, animated Aurora gradient layer, theme resources, zh-TW string resources.

**Prerequisites:** Phase 6 complete (tag `phase-6-update-complete`).

> Phase 7 transitions from headless TDD to UI work. WPF UI tests are brittle, so verification is **manual** in each task: run the app, confirm the described visual behaviour. Where logic can be extracted to a non-UI helper (e.g., the OS-version detector), we still write unit tests.

---

### Task 7.1: zh-TW string resources

**Files:**
- Create: `src/YtDlpTool/Resources/Strings.zh-TW.xaml`
- Create: `src/YtDlpTool/Resources/Strings.Designer.cs` (helper for code-behind access)

- [ ] **Step 1: Create string dictionary**

```xml
<!-- src/YtDlpTool/Resources/Strings.zh-TW.xaml -->
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                    xmlns:sys="clr-namespace:System;assembly=netstandard">

    <!-- Window -->
    <sys:String x:Key="App.Name">YtDlpTool</sys:String>
    <sys:String x:Key="App.Tagline">輕量 YouTube 下載工具</sys:String>

    <!-- URL input -->
    <sys:String x:Key="Url.Placeholder">貼上 YouTube 網址…</sys:String>
    <sys:String x:Key="Url.PasteButton">貼上</sys:String>
    <sys:String x:Key="Url.ClearButton">清除</sys:String>
    <sys:String x:Key="Url.Parsing">解析中…</sys:String>
    <sys:String x:Key="Url.InvalidNotYouTube">這不是 YouTube 網址哦，請確認</sys:String>

    <!-- Mode selector -->
    <sys:String x:Key="Mode.AudioOnly">純音訊</sys:String>
    <sys:String x:Key="Mode.AudioAndVideo">影音合併</sys:String>
    <sys:String x:Key="Mode.VideoOnly">純影像</sys:String>

    <!-- Quality dropdown -->
    <sys:String x:Key="Quality.Label">品質</sys:String>
    <sys:String x:Key="Quality.AudioHigh">高音質 M4A 256k</sys:String>
    <sys:String x:Key="Quality.AudioStandard">標準 M4A 128k</sys:String>
    <sys:String x:Key="Quality.Mp3_320">MP3 320k（轉碼）</sys:String>
    <sys:String x:Key="Quality.Mp3_192">MP3 192k（轉碼）</sys:String>

    <!-- Advanced -->
    <sys:String x:Key="Advanced.Title">進階選項</sys:String>
    <sys:String x:Key="Advanced.Hint">字幕 · 切片段</sys:String>
    <sys:String x:Key="Advanced.SubtitlesLabel">字幕</sys:String>
    <sys:String x:Key="Advanced.SubtitlesAutoSuffix">（自動）</sys:String>
    <sys:String x:Key="Advanced.SubtitlesLimit">最多選 3 個</sys:String>
    <sys:String x:Key="Advanced.ClipLabel">擷取片段</sys:String>
    <sys:String x:Key="Advanced.ClipStart">開始</sys:String>
    <sys:String x:Key="Advanced.ClipEnd">結束</sys:String>
    <sys:String x:Key="Advanced.ClipFormatHint">格式：hh:mm:ss</sys:String>

    <!-- Save location -->
    <sys:String x:Key="Save.Label">儲存位置</sys:String>
    <sys:String x:Key="Save.Browse">瀏覽…</sys:String>

    <!-- Main button -->
    <sys:String x:Key="Button.AddDownload">加入下載</sys:String>
    <sys:String x:Key="Button.NeedUrl">請先貼上網址</sys:String>
    <sys:String x:Key="Button.AddedFeedback">已加入佇列</sys:String>

    <!-- Queue -->
    <sys:String x:Key="Queue.Title">下載佇列</sys:String>
    <sys:String x:Key="Queue.Cancel">取消</sys:String>
    <sys:String x:Key="Queue.Retry">重試</sys:String>
    <sys:String x:Key="Queue.CopyError">複製錯誤</sys:String>
    <sys:String x:Key="Queue.OpenFolder">開啟資料夾</sys:String>

    <!-- Update banner -->
    <sys:String x:Key="Update.AvailableSingle">有新版本可更新</sys:String>
    <sys:String x:Key="Update.AvailableMulti">有 {0} 個元件可更新</sys:String>
    <sys:String x:Key="Update.OneClick">一鍵更新</sys:String>
    <sys:String x:Key="Update.Later">稍後</sys:String>
    <sys:String x:Key="Update.Downloading">下載中</sys:String>
    <sys:String x:Key="Update.Verifying">驗證中…</sys:String>
    <sys:String x:Key="Update.Applying">套用中…</sys:String>
    <sys:String x:Key="Update.Done">已更新</sys:String>
    <sys:String x:Key="Update.FailedRolledBack">更新失敗，已自動還原。點此查看詳情</sys:String>

    <!-- Settings -->
    <sys:String x:Key="Settings.Title">設定</sys:String>
    <sys:String x:Key="Settings.SectionDownload">下載</sys:String>
    <sys:String x:Key="Settings.SectionUpdate">更新</sys:String>
    <sys:String x:Key="Settings.SectionUi">介面</sys:String>
    <sys:String x:Key="Settings.SectionAdvanced">進階</sys:String>
    <sys:String x:Key="Settings.DefaultSaveDir">預設儲存資料夾</sys:String>
    <sys:String x:Key="Settings.Concurrency">並行下載數</sys:String>
    <sys:String x:Key="Settings.CheckFrequency">檢查頻率</sys:String>
    <sys:String x:Key="Settings.YtDlpUpdates">啟用 yt-dlp 自動更新</sys:String>
    <sys:String x:Key="Settings.FfmpegUpdates">啟用 ffmpeg 自動更新</sys:String>
    <sys:String x:Key="Settings.Language">語言</sys:String>
    <sys:String x:Key="Settings.Theme">主題</sys:String>
    <sys:String x:Key="Settings.OpenLogs">開啟日誌資料夾</sys:String>
    <sys:String x:Key="Settings.OpenDownloads">開啟下載資料夾</sys:String>
    <sys:String x:Key="Settings.About">關於</sys:String>

    <!-- Errors -->
    <sys:String x:Key="Error.NoWriteAccess">無法寫入此資料夾，請選其他位置</sys:String>
    <sys:String x:Key="Error.DiskFull">磁碟空間不足（剩餘 {0}, 需要約 {1}）</sys:String>
    <sys:String x:Key="Error.FileConflict">「{0}」已存在</sys:String>
    <sys:String x:Key="Error.Conflict.Overwrite">覆蓋</sys:String>
    <sys:String x:Key="Error.Conflict.Rename">改名為 {0}</sys:String>
    <sys:String x:Key="Error.Conflict.Cancel">取消</sys:String>

    <!-- First-launch hint -->
    <sys:String x:Key="Hint.FirstParse">品質下拉可選不同解析度 · 右下可開進階選項</sys:String>

</ResourceDictionary>
```

- [ ] **Step 2: Tiny code-behind helper (so views can call `Strings.Get("Url.Placeholder")` instead of `(string)Application.Current.FindResource(...)` everywhere)**

```csharp
// src/YtDlpTool/Resources/Strings.Designer.cs
using System.Windows;

namespace YtDlpTool.Resources;

public static class Strings
{
    public static string Get(string key)
    {
        var r = Application.Current?.TryFindResource(key);
        return r is string s ? s : key;
    }

    public static string Format(string key, params object[] args) =>
        string.Format(Get(key), args);
}
```

- [ ] **Step 3: Wire dictionary into App.xaml**

Replace `src/YtDlpTool/App.xaml`:

```xml
<Application x:Class="YtDlpTool.App"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             StartupUri="MainWindow.xaml">
    <Application.Resources>
        <ResourceDictionary>
            <ResourceDictionary.MergedDictionaries>
                <ResourceDictionary Source="/Resources/Strings.zh-TW.xaml" />
            </ResourceDictionary.MergedDictionaries>
        </ResourceDictionary>
    </Application.Resources>
</Application>
```

- [ ] **Step 4: Build**

```powershell
dotnet build src/YtDlpTool/
```
Expected: succeeds.

- [ ] **Step 5: Commit**

```powershell
git add src/YtDlpTool/Resources/Strings.zh-TW.xaml src/YtDlpTool/Resources/Strings.Designer.cs src/YtDlpTool/App.xaml
git commit -m "feat(ui): zh-TW string resources + Strings helper"
```

---

### Task 7.2: Theme resources (colors, brushes, spacing, typography)

**Files:**
- Create: `src/YtDlpTool/Resources/Theme.xaml`
- Modify: `src/YtDlpTool/App.xaml`

- [ ] **Step 1: Theme dictionary**

```xml
<!-- src/YtDlpTool/Resources/Theme.xaml -->
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                    xmlns:sys="clr-namespace:System;assembly=netstandard">

    <!-- Colours · Light -->
    <Color x:Key="Color.Accent">#2A2A3E</Color>
    <Color x:Key="Color.AccentHover">#3D3D55</Color>
    <Color x:Key="Color.Danger">#C45D5D</Color>
    <Color x:Key="Color.Success">#7EB89B</Color>
    <Color x:Key="Color.TextPrimary">#EB2A2A3E</Color>     <!-- alpha 92% -->
    <Color x:Key="Color.TextSecondary">#8C2A2A3E</Color>   <!-- alpha 55% -->
    <Color x:Key="Color.TextTertiary">#592A2A3E</Color>    <!-- alpha 35% -->
    <Color x:Key="Color.GlassBorder">#80FFFFFF</Color>
    <Color x:Key="Color.GlassFill">#A6FFFFFF</Color>
    <Color x:Key="Color.AuroraA">#A8C4E8</Color>
    <Color x:Key="Color.AuroraB">#D4B3D8</Color>
    <Color x:Key="Color.AuroraC">#F5C9B3</Color>

    <!-- Brushes (only ones used in 2+ places — others stay inline) -->
    <SolidColorBrush x:Key="Brush.Accent" Color="{StaticResource Color.Accent}" />
    <SolidColorBrush x:Key="Brush.AccentHover" Color="{StaticResource Color.AccentHover}" />
    <SolidColorBrush x:Key="Brush.Danger" Color="{StaticResource Color.Danger}" />
    <SolidColorBrush x:Key="Brush.Success" Color="{StaticResource Color.Success}" />
    <SolidColorBrush x:Key="Brush.TextPrimary" Color="{StaticResource Color.TextPrimary}" />
    <SolidColorBrush x:Key="Brush.TextSecondary" Color="{StaticResource Color.TextSecondary}" />
    <SolidColorBrush x:Key="Brush.TextTertiary" Color="{StaticResource Color.TextTertiary}" />
    <SolidColorBrush x:Key="Brush.GlassBorder" Color="{StaticResource Color.GlassBorder}" />
    <SolidColorBrush x:Key="Brush.GlassFill"   Color="{StaticResource Color.GlassFill}" />

    <!-- Spacing -->
    <sys:Double x:Key="Space.1">4</sys:Double>
    <sys:Double x:Key="Space.2">8</sys:Double>
    <sys:Double x:Key="Space.3">12</sys:Double>
    <sys:Double x:Key="Space.4">16</sys:Double>
    <sys:Double x:Key="Space.5">24</sys:Double>
    <sys:Double x:Key="Space.6">32</sys:Double>
    <sys:Double x:Key="Space.7">48</sys:Double>

    <!-- Typography -->
    <FontFamily x:Key="Font.Default">Microsoft JhengHei UI, Segoe UI Variable, Segoe UI</FontFamily>
    <FontFamily x:Key="Font.Numeric">Segoe UI Variable, Segoe UI, Consolas</FontFamily>

    <!-- Reusable styles -->
    <Style x:Key="GlassCard" TargetType="Border">
        <Setter Property="Background" Value="{StaticResource Brush.GlassFill}" />
        <Setter Property="BorderBrush" Value="{StaticResource Brush.GlassBorder}" />
        <Setter Property="BorderThickness" Value="1" />
        <Setter Property="CornerRadius" Value="14" />
        <Setter Property="Padding" Value="{StaticResource Space.5}" />
        <Setter Property="Effect">
            <Setter.Value>
                <DropShadowEffect Color="Black" Opacity="0.08" BlurRadius="20" ShadowDepth="2" />
            </Setter.Value>
        </Setter>
    </Style>

    <Style x:Key="PrimaryButton" TargetType="Button">
        <Setter Property="Background" Value="{StaticResource Brush.Accent}" />
        <Setter Property="Foreground" Value="White" />
        <Setter Property="FontFamily" Value="{StaticResource Font.Default}" />
        <Setter Property="FontSize" Value="14" />
        <Setter Property="FontWeight" Value="SemiBold" />
        <Setter Property="Height" Value="48" />
        <Setter Property="BorderThickness" Value="0" />
        <Setter Property="Cursor" Value="Hand" />
        <Setter Property="Template">
            <Setter.Value>
                <ControlTemplate TargetType="Button">
                    <Border x:Name="bg" Background="{TemplateBinding Background}" CornerRadius="12">
                        <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center" />
                    </Border>
                    <ControlTemplate.Triggers>
                        <Trigger Property="IsMouseOver" Value="True">
                            <Setter TargetName="bg" Property="Background" Value="{StaticResource Brush.AccentHover}" />
                        </Trigger>
                        <Trigger Property="IsEnabled" Value="False">
                            <Setter TargetName="bg" Property="Opacity" Value="0.45" />
                        </Trigger>
                    </ControlTemplate.Triggers>
                </ControlTemplate>
            </Setter.Value>
        </Setter>
    </Style>

</ResourceDictionary>
```

- [ ] **Step 2: Merge into App.xaml**

Replace `src/YtDlpTool/App.xaml`:

```xml
<Application x:Class="YtDlpTool.App"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             StartupUri="MainWindow.xaml">
    <Application.Resources>
        <ResourceDictionary>
            <ResourceDictionary.MergedDictionaries>
                <ResourceDictionary Source="/Resources/Strings.zh-TW.xaml" />
                <ResourceDictionary Source="/Resources/Theme.xaml" />
            </ResourceDictionary.MergedDictionaries>
        </ResourceDictionary>
    </Application.Resources>
</Application>
```

- [ ] **Step 3: Build**

```powershell
dotnet build src/YtDlpTool/
```
Expected: succeeds.

- [ ] **Step 4: Commit**

```powershell
git add src/YtDlpTool/Resources/Theme.xaml src/YtDlpTool/App.xaml
git commit -m "feat(ui): theme resources (colors, spacing, typography, GlassCard, PrimaryButton)"
```

---

### Task 7.3: Windows version detector

**Files:**
- Create: `src/YtDlpTool/Interop/WindowsVersion.cs`
- Create: `tests/YtDlpTool.Domain.Tests/Interop/WindowsVersionTests.cs` (placed in Domain.Tests for simplicity — pure helper)

Actually, to keep WPF types out of Domain.Tests, place the test inside a new Process.Tests folder or skip — the version detection logic uses `Environment.OSVersion` which is fine to test. Put it inline as a non-static class so we can pass an `OSVersion` override.

- [ ] **Step 1: Implement**

```csharp
// src/YtDlpTool/Interop/WindowsVersion.cs
namespace YtDlpTool.Interop;

public sealed record WindowsVersionInfo(int Major, int Build)
{
    public bool IsWin11OrLater => Major >= 10 && Build >= 22000;
    public bool SupportsAcrylic => Major >= 10 && Build >= 17763; // 1809
    public bool SupportsMica => IsWin11OrLater;
}

public static class WindowsVersion
{
    public static WindowsVersionInfo Current { get; } = ResolveCurrent();

    private static WindowsVersionInfo ResolveCurrent()
    {
        var os = Environment.OSVersion.Version;
        return new WindowsVersionInfo(os.Major, os.Build);
    }
}
```

- [ ] **Step 2: Build (no tests for this — it's a tiny pass-through)**

```powershell
dotnet build src/YtDlpTool/
```
Expected: succeeds.

- [ ] **Step 3: Commit**

```powershell
git add src/YtDlpTool/Interop/WindowsVersion.cs
git commit -m "feat(ui): Windows version detector for Mica/Acrylic fallback"
```

---

### Task 7.4: Mica/Acrylic window chrome interop

**Files:**
- Create: `src/YtDlpTool/Interop/WindowChromeHelper.cs`

DWM API P/Invokes. The `DwmSetWindowAttribute` `DWMWA_SYSTEMBACKDROP_TYPE` (Win11) and `SetWindowCompositionAttribute` (Win10) calls give us Mica/Acrylic without third-party libraries.

- [ ] **Step 1: Write the helper**

```csharp
// src/YtDlpTool/Interop/WindowChromeHelper.cs
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace YtDlpTool.Interop;

public static class WindowChromeHelper
{
    public static void ApplyAuroraBackdrop(Window window)
    {
        window.Background = Brushes.Transparent;
        var helper = new WindowInteropHelper(window);
        if (helper.Handle == IntPtr.Zero) helper.EnsureHandle();
        var hwnd = helper.Handle;

        if (WindowsVersion.Current.SupportsMica)
            TryApplyMica(hwnd);
        else if (WindowsVersion.Current.SupportsAcrylic)
            TryApplyAcrylic(hwnd);
        // else: stays transparent → MainWindow shows the Aurora gradient layer directly.
    }

    private static void TryApplyMica(IntPtr hwnd)
    {
        // DWMWA_SYSTEMBACKDROP_TYPE = 38; value 2 = Mica (main window)
        int backdropType = 2;
        DwmSetWindowAttribute(hwnd, 38, ref backdropType, sizeof(int));
        // Optional: extend frame into client area for full coverage
        var margins = new MARGINS { cxLeftWidth = -1, cxRightWidth = -1, cyTopHeight = -1, cyBottomHeight = -1 };
        DwmExtendFrameIntoClientArea(hwnd, ref margins);
    }

    private static void TryApplyAcrylic(IntPtr hwnd)
    {
        var accent = new ACCENT_POLICY
        {
            AccentState = 4, // ACCENT_ENABLE_ACRYLICBLURBEHIND
            GradientColor = 0x99_F0_F0_F0,
        };
        var size = Marshal.SizeOf(accent);
        var ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(accent, ptr, fDeleteOld: false);
            var data = new WINDOWCOMPOSITIONATTRIBDATA
            {
                Attribute = 19, // WCA_ACCENT_POLICY
                SizeOfData = size,
                Data = ptr
            };
            SetWindowCompositionAttribute(hwnd, ref data);
        }
        finally { Marshal.FreeHGlobal(ptr); }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS pMarInset);

    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WINDOWCOMPOSITIONATTRIBDATA data);

    [StructLayout(LayoutKind.Sequential)] private struct MARGINS { public int cxLeftWidth, cxRightWidth, cyTopHeight, cyBottomHeight; }
    [StructLayout(LayoutKind.Sequential)] private struct ACCENT_POLICY { public int AccentState; public int AccentFlags; public uint GradientColor; public int AnimationId; }
    [StructLayout(LayoutKind.Sequential)] private struct WINDOWCOMPOSITIONATTRIBDATA { public int Attribute; public IntPtr Data; public int SizeOfData; }
}
```

- [ ] **Step 2: Build**

```powershell
dotnet build src/YtDlpTool/
```
Expected: succeeds.

- [ ] **Step 3: Commit**

```powershell
git add src/YtDlpTool/Interop/WindowChromeHelper.cs
git commit -m "feat(ui): WindowChromeHelper P/Invoke for Mica (Win11) and Acrylic (Win10 1809+)"
```

---

### Task 7.5: Aurora gradient animation layer

**Files:**
- Create: `src/YtDlpTool/Views/AuroraBackground.xaml`
- Create: `src/YtDlpTool/Views/AuroraBackground.xaml.cs`

A `UserControl` placed beneath all content. Three `GradientStop`s slowly move offsets between values, producing a calm breathing effect.

- [ ] **Step 1: XAML**

```xml
<!-- src/YtDlpTool/Views/AuroraBackground.xaml -->
<UserControl x:Class="YtDlpTool.Views.AuroraBackground"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             IsHitTestVisible="False">
    <UserControl.Resources>
        <Storyboard x:Key="AuroraStoryboard" RepeatBehavior="Forever" AutoReverse="True">
            <DoubleAnimation Storyboard.TargetName="StopA" Storyboard.TargetProperty="Offset"
                             From="0.0" To="0.4" Duration="0:0:30" />
            <DoubleAnimation Storyboard.TargetName="StopB" Storyboard.TargetProperty="Offset"
                             From="0.5" To="0.7" Duration="0:0:30" />
            <DoubleAnimation Storyboard.TargetName="StopC" Storyboard.TargetProperty="Offset"
                             From="1.0" To="0.85" Duration="0:0:30" />
        </Storyboard>
    </UserControl.Resources>
    <Grid>
        <Rectangle Opacity="0.35">
            <Rectangle.Fill>
                <LinearGradientBrush StartPoint="0,0" EndPoint="1,1">
                    <GradientStop x:Name="StopA" Color="{StaticResource Color.AuroraA}" Offset="0.0" />
                    <GradientStop x:Name="StopB" Color="{StaticResource Color.AuroraB}" Offset="0.5" />
                    <GradientStop x:Name="StopC" Color="{StaticResource Color.AuroraC}" Offset="1.0" />
                </LinearGradientBrush>
            </Rectangle.Fill>
        </Rectangle>
    </Grid>
</UserControl>
```

- [ ] **Step 2: Code-behind**

```csharp
// src/YtDlpTool/Views/AuroraBackground.xaml.cs
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;

namespace YtDlpTool.Views;

public partial class AuroraBackground : UserControl
{
    public AuroraBackground()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var sb = (Storyboard)Resources["AuroraStoryboard"];
        sb.Begin(this, isControllable: true);
    }
}
```

- [ ] **Step 3: Build**

```powershell
dotnet build src/YtDlpTool/
```
Expected: succeeds.

- [ ] **Step 4: Commit**

```powershell
git add src/YtDlpTool/Views/AuroraBackground.xaml src/YtDlpTool/Views/AuroraBackground.xaml.cs
git commit -m "feat(ui): AuroraBackground animated gradient layer"
```

---

### Task 7.6: Composition root — `AppHost` wires Domain services

**Files:**
- Create: `src/YtDlpTool/AppHost.cs`

`AppHost` is the place we wire concrete services (paths, config, logger, queue, update checker). The `App.xaml.cs` calls into it on `Startup`. Avoids a DI container (overkill for this size) while keeping `MainWindow.xaml.cs` thin.

- [ ] **Step 1: Implement**

```csharp
// src/YtDlpTool/AppHost.cs
using YtDlpTool.Domain.Logging;
using YtDlpTool.Domain.Models;
using YtDlpTool.Domain.Persistence;
using YtDlpTool.Domain.Security;
using YtDlpTool.Domain.Services;
using YtDlpTool.Domain.Updates;
using YtDlpTool.Process;

namespace YtDlpTool;

public sealed class AppHost : IDisposable
{
    public AppPaths Paths { get; }
    public AppConfig Config { get; private set; }
    public ConfigStore ConfigStore { get; }
    public AppLogger Logger { get; }
    public StateJournal StateJournal { get; }
    public YtDlpRunner YtDlp { get; }
    public FfmpegRunner Ffmpeg { get; }
    public HttpUpdateClient UpdateHttp { get; }
    public UpdateChecker UpdateChecker { get; }
    public UpdateApplier UpdateApplier { get; }
    public DownloadQueue Queue { get; }

    public AppHost()
    {
        Paths = AppPaths.ResolveForCurrentProcess();
        Paths.EnsureDataDirectoriesExist();

        ConfigStore = new ConfigStore(Paths.ConfigFile);
        Config = ConfigStore.Load();
        if (string.IsNullOrWhiteSpace(Config.DefaultSaveDirectory))
        {
            Config.DefaultSaveDirectory = AppConfig.CreateDefault().DefaultSaveDirectory;
            ConfigStore.Save(Config);
        }
        Directory.CreateDirectory(Config.DefaultSaveDirectory);

        Logger = new AppLogger(
            Paths.LogsDirectory,
            ParseLogLevel(Config.LogLevel),
            () => DateTime.Now);
        AppLogger.PurgeOlderThan(Paths.LogsDirectory, TimeSpan.FromDays(7), DateTime.Now);

        StateJournal = new StateJournal(Paths.StateLog);

        var ytDlpExe  = Path.Combine(Paths.BinDirectory, "yt-dlp.exe");
        var ffmpegExe = Path.Combine(Paths.BinDirectory, "ffmpeg.exe");
        YtDlp  = new YtDlpRunner(ytDlpExe);
        Ffmpeg = new FfmpegRunner(ffmpegExe);

        UpdateHttp = new HttpUpdateClient($"YtDlpTool/{ThisVersion()}");

        var sigstoreOpts = new SigstoreVerifierOptions(
            ExpectedIssuer: "https://token.actions.githubusercontent.com",
            // Owner/repo filled in by Phase 10 release workflow; here it's a placeholder
            // that will match nothing in dev builds — verifier returns Fail, UpdateChecker swallows it.
            ExpectedSanRegex: @"^https://github\.com/OWNER/REPO/\.github/workflows/release\.yml@refs/tags/v.*$",
            TrustedRootPem: SigstoreRoots.FulcioRootPem);

        UpdateChecker = new UpdateChecker(UpdateHttp, sigstoreOpts, owner: "OWNER", repo: "REPO");
        UpdateApplier = new UpdateApplier(UpdateHttp, sigstoreOpts, Paths);

        var executor = new YtDlpDownloadExecutor(YtDlp);
        var journaledOnEvent = JournaledQueue.Wrap(StateJournal, OnQueueEvent);
        Queue = new DownloadQueue(executor, Config.ConcurrentDownloads, journaledOnEvent);
    }

    public event EventHandler<QueueEvent>? QueueEventRaised;

    private void OnQueueEvent(QueueEvent evt)
    {
        QueueEventRaised?.Invoke(this, evt);
    }

    private static LogLevel ParseLogLevel(string s) => s switch
    {
        "Debug" => LogLevel.Debug,
        "Info"  => LogLevel.Info,
        "Warn"  => LogLevel.Warn,
        "Error" => LogLevel.Error,
        _       => LogLevel.Info
    };

    private static string ThisVersion() =>
        System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0";

    public void Dispose()
    {
        Queue.Dispose();
        UpdateHttp.Dispose();
        StateJournal.Dispose();
        Logger.Dispose();
    }
}
```

- [ ] **Step 2: Create the executor that bridges the Queue to `YtDlpRunner`**

```csharp
// src/YtDlpTool/YtDlpDownloadExecutor.cs
using YtDlpTool.Domain.Models;
using YtDlpTool.Domain.Services;
using YtDlpTool.Process;

namespace YtDlpTool;

public sealed class YtDlpDownloadExecutor : IDownloadExecutor
{
    private readonly YtDlpRunner _runner;
    public YtDlpDownloadExecutor(YtDlpRunner runner) => _runner = runner;

    public async Task<DownloadExecutionResult> ExecuteAsync(
        DownloadJob job,
        IProgress<DownloadProgressSnapshot> progress,
        CancellationToken cancellationToken)
    {
        var request = new DownloadRequest(
            Url: job.Url,
            Mode: job.Mode,
            ChosenFormat: job.ChosenFormat,
            SubtitleLanguageCodes: job.SubtitleLanguageCodes,
            ClipRange: job.ClipRange,
            SaveDirectory: job.SaveDirectory,
            SanitizedFileStem: FileNameSanitizer.Sanitize(job.Title));

        var processProgress = new Progress<ProgressReport>(p =>
            progress.Report(new DownloadProgressSnapshot(p.Percent, p.BytesPerSecond, p.Eta)));

        var result = await _runner.DownloadAsync(request, processProgress, cancellationToken).ConfigureAwait(false);

        if (result.WasCancelled)
            return new DownloadExecutionResult(false, null, null, true);
        if (!result.IsSuccess)
        {
            var mapped = ErrorMapper.Map(result.ErrorStderr ?? "");
            return new DownloadExecutionResult(false, null, mapped, false);
        }
        return new DownloadExecutionResult(true, result.OutputFilePath, null, false);
    }
}
```

- [ ] **Step 3: Build**

```powershell
dotnet build src/YtDlpTool/
```
Expected: succeeds.

- [ ] **Step 4: Commit**

```powershell
git add src/YtDlpTool/AppHost.cs src/YtDlpTool/YtDlpDownloadExecutor.cs
git commit -m "feat(ui): AppHost composition root + YtDlpDownloadExecutor adapter"
```

---

### Task 7.7: New `MainWindow` shell with global handlers

**Files:**
- Modify: `src/YtDlpTool/App.xaml.cs`
- Modify: `src/YtDlpTool/MainWindow.xaml`
- Modify: `src/YtDlpTool/MainWindow.xaml.cs`

> This is the **outer shell only** — title bar, Aurora background, container regions for the components to be filled in Phase 8. Each child region is left as an empty named `Border` Phase 8 will replace.

- [ ] **Step 1: Edit `App.xaml.cs` for global error handlers and AppHost lifecycle**

```csharp
// src/YtDlpTool/App.xaml.cs
using System.Windows;
using System.Windows.Threading;

namespace YtDlpTool;

public partial class App : Application
{
    public AppHost? Host { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnDispatcherException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainException;
        TaskScheduler.UnobservedTaskException += OnTaskException;

        Host = new AppHost();
        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Host?.Dispose();
        base.OnExit(e);
    }

    private void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Host?.Logger.Error("unhandled.dispatcher", new Dictionary<string, string>
        {
            ["type"] = e.Exception.GetType().Name,
            ["msg"]  = e.Exception.Message
        });
        MessageBox.Show("程式遇到了未預期的問題，已保存錯誤記錄。按確定關閉。",
            "YtDlpTool", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
        Shutdown(1);
    }

    private void OnDomainException(object sender, UnhandledExceptionEventArgs e) =>
        Host?.Logger.Error("unhandled.domain",
            e.ExceptionObject is Exception ex
                ? new Dictionary<string, string> { ["type"] = ex.GetType().Name, ["msg"] = ex.Message }
                : new Dictionary<string, string> { ["msg"] = "non-exception object" });

    private void OnTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Host?.Logger.Warn("unhandled.task", new Dictionary<string, string>
        {
            ["msg"] = e.Exception.Message
        });
        e.SetObserved();
    }
}
```

- [ ] **Step 2: Replace `MainWindow.xaml`**

```xml
<!-- src/YtDlpTool/MainWindow.xaml -->
<Window x:Class="YtDlpTool.MainWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:views="clr-namespace:YtDlpTool.Views"
        Title="{StaticResource App.Name}"
        Width="1280" Height="800" MinWidth="900" MinHeight="600"
        WindowStartupLocation="CenterScreen"
        AllowsTransparency="False"
        Background="Transparent"
        FontFamily="{StaticResource Font.Default}"
        FontSize="14">

    <Grid>
        <!-- Aurora animated layer behind everything -->
        <views:AuroraBackground />

        <!-- Update banner host (empty until Phase 9 fills it) -->
        <Border x:Name="UpdateBannerHost"
                VerticalAlignment="Top"
                Visibility="Collapsed" />

        <!-- Main content -->
        <Grid Margin="{StaticResource Space.5}">
            <Grid.RowDefinitions>
                <RowDefinition Height="Auto" />   <!-- top bar -->
                <RowDefinition Height="*" />      <!-- form -->
            </Grid.RowDefinitions>

            <!-- Top bar -->
            <Grid Grid.Row="0" Height="56">
                <Grid.ColumnDefinitions>
                    <ColumnDefinition Width="Auto" />
                    <ColumnDefinition Width="*" />
                    <ColumnDefinition Width="Auto" />
                </Grid.ColumnDefinitions>
                <StackPanel Grid.Column="0" Orientation="Horizontal" VerticalAlignment="Center">
                    <Ellipse Width="20" Height="20" Margin="0,0,8,0">
                        <Ellipse.Fill>
                            <LinearGradientBrush>
                                <GradientStop Color="{StaticResource Color.AuroraA}" Offset="0" />
                                <GradientStop Color="{StaticResource Color.AuroraB}" Offset="0.5" />
                                <GradientStop Color="{StaticResource Color.AuroraC}" Offset="1" />
                            </LinearGradientBrush>
                        </Ellipse.Fill>
                    </Ellipse>
                    <TextBlock Text="{StaticResource App.Name}"
                               FontSize="16" FontWeight="SemiBold"
                               Foreground="{StaticResource Brush.TextPrimary}"
                               VerticalAlignment="Center" />
                </StackPanel>
                <Button x:Name="SettingsButton" Grid.Column="2"
                        Width="32" Height="32"
                        Background="Transparent" BorderThickness="0"
                        Content="⚙"
                        FontSize="16"
                        ToolTip="{StaticResource Settings.Title}"
                        Click="OnSettingsClicked" />
            </Grid>

            <!-- Form: hosts URL input, format selector, advanced options, save location, button, queue -->
            <ScrollViewer Grid.Row="1" VerticalScrollBarVisibility="Auto" Margin="0,16,0,0">
                <StackPanel x:Name="FormHost" />
            </ScrollViewer>
        </Grid>
    </Grid>
</Window>
```

- [ ] **Step 3: Replace `MainWindow.xaml.cs`**

```csharp
// src/YtDlpTool/MainWindow.xaml.cs
using System.Windows;
using YtDlpTool.Interop;

namespace YtDlpTool;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => WindowChromeHelper.ApplyAuroraBackdrop(this);
    }

    private void OnSettingsClicked(object sender, RoutedEventArgs e)
    {
        // Phase 9 wires this up to SettingsDialog.
        MessageBox.Show("設定對話框將在 Phase 9 完成", "YtDlpTool");
    }
}
```

- [ ] **Step 4: Manual smoke run**

```powershell
dotnet run --project src/YtDlpTool/
```
Expected:
- Window opens 1280×800
- On Win11: Mica background visible
- On Win10 1809+: Acrylic background visible
- Aurora gradient slowly breathes across the window
- Top bar shows logo + app name + settings gear
- Form area is empty (Phase 8 fills it)

Close the window.

- [ ] **Step 5: Commit**

```powershell
git add src/YtDlpTool/App.xaml.cs src/YtDlpTool/MainWindow.xaml src/YtDlpTool/MainWindow.xaml.cs
git commit -m "feat(ui): real MainWindow shell with Mica/Acrylic + Aurora + top bar"
```

---

### Task 7.8: AOT publish verification

- [ ] **Step 1: Publish**

```powershell
dotnet publish src/YtDlpTool/YtDlpTool.csproj -c Release -r win-x64
```
Expected: succeeds. Important: WPF + AOT can surface trimming warnings here. If any `IL2026`/`IL3050` appear, suppress with `[DynamicallyAccessedMembers]` or `[UnconditionalSuppressMessage]` and document — but for the code in this phase no such warnings should appear.

- [ ] **Step 2: Run published exe to confirm**

```powershell
Start-Process src/YtDlpTool/bin/Release/net8.0-windows10.0.19041.0/win-x64/publish/YtDlpTool.exe
```
Expected: same shell appears with Mica/Acrylic + Aurora.

- [ ] **Step 3: Tag**

```powershell
git tag phase-7-shell-complete
```

---

## Phase 7 complete gate

- [ ] String resources in zh-TW
- [ ] Theme resources (colors, brushes, spacing, type, GlassCard, PrimaryButton)
- [ ] `WindowsVersion` detector
- [ ] `WindowChromeHelper` for Mica/Acrylic
- [ ] `AuroraBackground` animated layer
- [ ] `AppHost` composition root
- [ ] `MainWindow` shell with global exception handlers
- [ ] Manual smoke test: window opens with backdrop + Aurora
- [ ] AOT publish green
- [ ] Tag `phase-7-shell-complete`

Proceed to Phase 8.

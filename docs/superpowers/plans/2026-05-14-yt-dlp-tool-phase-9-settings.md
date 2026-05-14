# Phase 9 · Update Banner · Settings Dialog · Toast Notifications

**Goal:** Wire the update banner that slides in when `UpdateChecker` reports a newer release, complete the settings dialog with all config bindings, and add Windows toast notifications for completed downloads.

**Prerequisites:** Phase 8 complete (tag `phase-8-components-complete`).

---

### Task 9.1: Background update check on startup

**Files:**
- Modify: `src/YtDlpTool/AppHost.cs`
- Create: `src/YtDlpTool/ViewModels/UpdateBannerViewModel.cs`

- [ ] **Step 1: Add `UpdateBannerViewModel`**

```csharp
// src/YtDlpTool/ViewModels/UpdateBannerViewModel.cs
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using YtDlpTool.Domain.Updates;

namespace YtDlpTool.ViewModels;

public partial class UpdateBannerViewModel : ObservableObject
{
    [ObservableProperty] private bool _isVisible;
    [ObservableProperty] private string _headline = "";
    [ObservableProperty] private bool _isApplying;
    [ObservableProperty] private string _applyStatus = "";
    [ObservableProperty] private double _applyPercent;
    [ObservableProperty] private bool _hasFailure;
    [ObservableProperty] private string? _failureReason;
    public ObservableCollection<ManifestFileEntry> Entries { get; } = new();
}
```

- [ ] **Step 2: Add update check wiring to `AppHost`**

Append to the bottom of `AppHost`, just before `Dispose`:

```csharp
    public UpdateBannerViewModel BannerVm { get; } = new();

    public async Task StartBackgroundUpdateCheckAsync(CancellationToken ct)
    {
        // Wait 60 seconds after startup before first check (spec 4.3).
        try { await Task.Delay(TimeSpan.FromSeconds(60), ct).ConfigureAwait(false); }
        catch (TaskCanceledException) { return; }

        if (!ShouldCheckNow(Config)) return;

        var installed = new InstalledVersions(
            App: ThisVersion(),
            YtDlp: ProbeYtDlpVersion(),
            Ffmpeg: ProbeFfmpegVersion());

        var availability = await UpdateChecker.CheckAsync(installed, ct).ConfigureAwait(false);

        if (availability.HasUpdate && availability.NewerFiles.Count > 0)
        {
            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                BannerVm.Entries.Clear();
                foreach (var f in availability.NewerFiles) BannerVm.Entries.Add(f);
                BannerVm.Headline = availability.NewerFiles.Count == 1
                    ? $"有新版本可更新 · {availability.NewerFiles[0].Name} {availability.NewerFiles[0].Version}"
                    : $"有 {availability.NewerFiles.Count} 個元件可更新";
                BannerVm.IsVisible = true;
            });
        }

        Config.LastAppCheck = DateTimeOffset.UtcNow;
        ConfigStore.Save(Config);
    }

    private static bool ShouldCheckNow(AppConfig cfg)
    {
        if (cfg.AppCheckFrequency == UpdateCheckFrequency.Never) return false;
        if (cfg.LastAppCheck is null) return true;
        var elapsed = DateTimeOffset.UtcNow - cfg.LastAppCheck.Value;
        return cfg.AppCheckFrequency switch
        {
            UpdateCheckFrequency.EveryLaunch => true,
            UpdateCheckFrequency.Daily       => elapsed >= TimeSpan.FromDays(1),
            UpdateCheckFrequency.Weekly      => elapsed >= TimeSpan.FromDays(7),
            UpdateCheckFrequency.Monthly     => elapsed >= TimeSpan.FromDays(30),
            _ => false
        };
    }

    private string ProbeYtDlpVersion()
    {
        // Probe by running `--version` synchronously with a tiny timeout. Best-effort.
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = Path.Combine(Paths.BinDirectory, "yt-dlp.exe"),
                Arguments = "--version",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = System.Diagnostics.Process.Start(psi);
            if (p is null) return "";
            if (!p.WaitForExit(2000)) { try { p.Kill(); } catch { } return ""; }
            return p.StandardOutput.ReadToEnd().Trim();
        }
        catch { return ""; }
    }

    private string ProbeFfmpegVersion()
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = Path.Combine(Paths.BinDirectory, "ffmpeg.exe"),
                Arguments = "-version",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = System.Diagnostics.Process.Start(psi);
            if (p is null) return "";
            if (!p.WaitForExit(2000)) { try { p.Kill(); } catch { } return ""; }
            var firstLine = p.StandardOutput.ReadLine() ?? "";
            // "ffmpeg version 7.1 ..." → take "7.1"
            var parts = firstLine.Split(' ');
            return parts.Length >= 3 ? parts[2] : "";
        }
        catch { return ""; }
    }
```

- [ ] **Step 3: Kick off in `App.xaml.cs`**

Modify `OnStartup` in `App.xaml.cs`:

```csharp
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnDispatcherException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainException;
        TaskScheduler.UnobservedTaskException += OnTaskException;

        Host = new AppHost();
        base.OnStartup(e);

        _ = Host.StartBackgroundUpdateCheckAsync(_appShutdown.Token);
    }

    private readonly CancellationTokenSource _appShutdown = new();

    protected override void OnExit(ExitEventArgs e)
    {
        _appShutdown.Cancel();
        Host?.Dispose();
        _appShutdown.Dispose();
        base.OnExit(e);
    }
```

- [ ] **Step 4: Build**

```powershell
dotnet build src/YtDlpTool/
```
Expected: succeeds.

- [ ] **Step 5: Commit**

```powershell
git add src/YtDlpTool/AppHost.cs src/YtDlpTool/App.xaml.cs src/YtDlpTool/ViewModels/UpdateBannerViewModel.cs
git commit -m "feat(ui): background update check 60s after startup with frequency policy"
```

---

### Task 9.2: `UpdateBannerView`

**Files:**
- Create: `src/YtDlpTool/Views/UpdateBannerView.xaml`
- Create: `src/YtDlpTool/Views/UpdateBannerView.xaml.cs`
- Modify: `src/YtDlpTool/MainWindow.xaml` (replace placeholder UpdateBannerHost)
- Modify: `src/YtDlpTool/MainWindow.xaml.cs`

- [ ] **Step 1: XAML**

```xml
<!-- src/YtDlpTool/Views/UpdateBannerView.xaml -->
<UserControl x:Class="YtDlpTool.Views.UpdateBannerView"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             Visibility="{Binding IsVisible, Converter={StaticResource BoolToVisibilityConverter}}"
             Height="56">
    <Border Background="{StaticResource Brush.Accent}" CornerRadius="0,0,12,12">
        <Grid Margin="24,0">
            <Grid.ColumnDefinitions>
                <ColumnDefinition Width="*" />
                <ColumnDefinition Width="Auto" />
                <ColumnDefinition Width="Auto" />
            </Grid.ColumnDefinitions>

            <!-- Idle state -->
            <TextBlock Grid.Column="0"
                       Text="{Binding Headline}"
                       Foreground="White"
                       VerticalAlignment="Center"
                       Visibility="{Binding IsApplying, Converter={StaticResource InverseBoolToVisibilityConverter}}" />

            <!-- Applying state -->
            <StackPanel Grid.Column="0" Orientation="Vertical" VerticalAlignment="Center"
                        Visibility="{Binding IsApplying, Converter={StaticResource BoolToVisibilityConverter}}">
                <TextBlock Foreground="White" Text="{Binding ApplyStatus}" />
                <ProgressBar Height="3" Margin="0,4,0,0"
                             Value="{Binding ApplyPercent}" Foreground="White" />
            </StackPanel>

            <Button Grid.Column="1"
                    Content="一鍵更新"
                    Background="White"
                    Foreground="{StaticResource Brush.Accent}"
                    Padding="14,6"
                    Margin="12,0"
                    BorderThickness="0"
                    Cursor="Hand"
                    Click="OnOneClickClicked"
                    Visibility="{Binding IsApplying, Converter={StaticResource InverseBoolToVisibilityConverter}}" />

            <Button Grid.Column="2"
                    Content="✕"
                    Background="Transparent"
                    Foreground="White"
                    BorderThickness="0"
                    Cursor="Hand"
                    Click="OnLaterClicked" />
        </Grid>
    </Border>
</UserControl>
```

- [ ] **Step 2: Code-behind**

```csharp
// src/YtDlpTool/Views/UpdateBannerView.xaml.cs
using System.Windows;
using System.Windows.Controls;
using YtDlpTool.Domain.Updates;
using YtDlpTool.ViewModels;

namespace YtDlpTool.Views;

public partial class UpdateBannerView : UserControl
{
    private AppHost? Host => (Application.Current as App)?.Host;
    private UpdateBannerViewModel? Vm => DataContext as UpdateBannerViewModel;

    public UpdateBannerView()
    {
        InitializeComponent();
    }

    private async void OnOneClickClicked(object sender, RoutedEventArgs e)
    {
        if (Host is null || Vm is null) return;
        Vm.IsApplying = true;
        Vm.ApplyStatus = "下載中…";
        Vm.ApplyPercent = 0;

        var progress = new Progress<UpdateApplyProgress>(p =>
        {
            Vm.ApplyStatus = p.Stage switch
            {
                UpdateApplyStage.Downloading       => $"下載中 · {p.FileName} · {p.FilePercent:0}%",
                UpdateApplyStage.VerifyingHash     => $"驗證雜湊 · {p.FileName}",
                UpdateApplyStage.VerifyingSignature => $"驗證簽章 · {p.FileName}",
                UpdateApplyStage.Applying          => $"套用中 · {p.FileName}",
                UpdateApplyStage.Done              => "✓ 已更新",
                UpdateApplyStage.RolledBack        => "已還原",
                _ => Vm.ApplyStatus
            };
            Vm.ApplyPercent = (double)(p.FileIndex - 1) / Math.Max(1, p.FileCount) * 100 + p.FilePercent / p.FileCount;
        });

        var result = await Host.UpdateApplier.ApplyAsync(
            Vm.Entries.ToList(), progress, CancellationToken.None);

        if (result.IsSuccess)
        {
            Vm.ApplyStatus = "✓ 已更新";
            await Task.Delay(1500);
            Vm.IsVisible = false;
            Vm.IsApplying = false;
        }
        else
        {
            Vm.IsApplying = false;
            Vm.HasFailure = true;
            Vm.FailureReason = result.FailureReason;
            MessageBox.Show(
                $"更新失敗，已自動還原。\n\n詳情：{result.FailureReason}",
                "YtDlpTool", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnLaterClicked(object sender, RoutedEventArgs e)
    {
        if (Vm is not null) Vm.IsVisible = false;
    }
}
```

- [ ] **Step 3: Add boolean→visibility converters**

```csharp
// src/YtDlpTool/Views/Converters/BoolToVisibilityConverter.cs
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace YtDlpTool.Views.Converters;

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is Visibility.Visible;
}

public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is Visibility.Collapsed;
}
```

- [ ] **Step 4: Register converters in `App.xaml`**

Add to the existing converter declarations (the `xmlns:conv` is already declared at the `Application` root from Phase 8 task 8.7, so just add the two new resource lines):

```xml
            <conv:BoolToVisibilityConverter x:Key="BoolToVisibilityConverter" />
            <conv:InverseBoolToVisibilityConverter x:Key="InverseBoolToVisibilityConverter" />
```

- [ ] **Step 5: Replace `UpdateBannerHost` in MainWindow.xaml**

```xml
        <!-- Update banner host -->
        <views:UpdateBannerView x:Name="UpdateBanner" VerticalAlignment="Top" />
```

In `MainWindow.xaml.cs` `OnLoaded`:

```csharp
        UpdateBanner.DataContext = host.BannerVm;
```

- [ ] **Step 6: Smoke + commit**

```powershell
dotnet run --project src/YtDlpTool/
```
(Banner won't actually appear without a published release — but verify it doesn't break the layout.)

```powershell
git add src/YtDlpTool/Views/UpdateBannerView.xaml src/YtDlpTool/Views/UpdateBannerView.xaml.cs src/YtDlpTool/Views/Converters/BoolToVisibilityConverter.cs src/YtDlpTool/App.xaml src/YtDlpTool/MainWindow.xaml src/YtDlpTool/MainWindow.xaml.cs
git commit -m "feat(ui): UpdateBannerView with one-click update + progress display"
```

---

### Task 9.3: `SettingsDialog`

**Files:**
- Create: `src/YtDlpTool/Dialogs/SettingsDialog.xaml`
- Create: `src/YtDlpTool/Dialogs/SettingsDialog.xaml.cs`
- Modify: `src/YtDlpTool/MainWindow.xaml.cs` (wire `OnSettingsClicked`)

- [ ] **Step 1: XAML**

```xml
<!-- src/YtDlpTool/Dialogs/SettingsDialog.xaml -->
<Window x:Class="YtDlpTool.Dialogs.SettingsDialog"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="{StaticResource Settings.Title}"
        Width="560" Height="640"
        WindowStartupLocation="CenterOwner"
        WindowStyle="ToolWindow"
        ResizeMode="NoResize"
        Background="#F4F2EE"
        FontFamily="{StaticResource Font.Default}"
        FontSize="14">
    <Grid Margin="24">
        <Grid.RowDefinitions>
            <RowDefinition Height="*" />
            <RowDefinition Height="Auto" />
        </Grid.RowDefinitions>

        <ScrollViewer Grid.Row="0" VerticalScrollBarVisibility="Auto">
            <StackPanel>
                <!-- Download -->
                <TextBlock Text="{StaticResource Settings.SectionDownload}" FontWeight="SemiBold" />
                <Grid Margin="0,8,0,16">
                    <Grid.ColumnDefinitions>
                        <ColumnDefinition Width="180" />
                        <ColumnDefinition Width="*" />
                    </Grid.ColumnDefinitions>
                    <Grid.RowDefinitions>
                        <RowDefinition />
                        <RowDefinition />
                    </Grid.RowDefinitions>

                    <TextBlock Grid.Row="0" Grid.Column="0" Text="{StaticResource Settings.DefaultSaveDir}" VerticalAlignment="Center" />
                    <StackPanel Grid.Row="0" Grid.Column="1" Orientation="Horizontal">
                        <TextBlock x:Name="DefaultDirText" Width="280" TextTrimming="CharacterEllipsis" VerticalAlignment="Center" />
                        <Button Content="{StaticResource Save.Browse}" Click="OnBrowseDefaultDir" Margin="8,0,0,0" />
                    </StackPanel>

                    <TextBlock Grid.Row="1" Grid.Column="0" Text="{StaticResource Settings.Concurrency}" VerticalAlignment="Center" />
                    <StackPanel Grid.Row="1" Grid.Column="1" Orientation="Horizontal">
                        <Slider x:Name="ConcurrencySlider" Width="200" Minimum="1" Maximum="5" TickPlacement="BottomRight"
                                IsSnapToTickEnabled="True" TickFrequency="1" />
                        <TextBlock Margin="8,0,0,0" Text="{Binding Value, ElementName=ConcurrencySlider}" VerticalAlignment="Center" />
                    </StackPanel>
                </Grid>

                <!-- Update -->
                <TextBlock Text="{StaticResource Settings.SectionUpdate}" FontWeight="SemiBold" Margin="0,8,0,0" />
                <StackPanel Margin="0,8,0,16">
                    <ComboBox x:Name="FrequencyCombo" Width="220" HorizontalAlignment="Left">
                        <ComboBoxItem Content="每次啟動" Tag="EveryLaunch" />
                        <ComboBoxItem Content="每日"    Tag="Daily" />
                        <ComboBoxItem Content="每週"    Tag="Weekly" />
                        <ComboBoxItem Content="每月"    Tag="Monthly" />
                        <ComboBoxItem Content="不檢查"  Tag="Never" />
                    </ComboBox>
                    <CheckBox x:Name="EnableYtDlpUpdates" Content="{StaticResource Settings.YtDlpUpdates}" Margin="0,8,0,0" />
                    <CheckBox x:Name="EnableFfmpegUpdates" Content="{StaticResource Settings.FfmpegUpdates}" Margin="0,4,0,0" />
                </StackPanel>

                <!-- UI -->
                <TextBlock Text="{StaticResource Settings.SectionUi}" FontWeight="SemiBold" Margin="0,8,0,0" />
                <Grid Margin="0,8,0,16">
                    <Grid.ColumnDefinitions>
                        <ColumnDefinition Width="180" />
                        <ColumnDefinition Width="*" />
                    </Grid.ColumnDefinitions>
                    <Grid.RowDefinitions>
                        <RowDefinition />
                        <RowDefinition />
                    </Grid.RowDefinitions>
                    <TextBlock Grid.Row="0" Grid.Column="0" Text="{StaticResource Settings.Language}" VerticalAlignment="Center" />
                    <ComboBox Grid.Row="0" Grid.Column="1" SelectedIndex="0" Width="200" HorizontalAlignment="Left">
                        <ComboBoxItem Content="繁體中文" />
                    </ComboBox>
                    <TextBlock Grid.Row="1" Grid.Column="0" Text="{StaticResource Settings.Theme}" VerticalAlignment="Center" />
                    <ComboBox x:Name="ThemeCombo" Grid.Row="1" Grid.Column="1" Width="200" HorizontalAlignment="Left">
                        <ComboBoxItem Content="跟隨系統" Tag="System" />
                        <ComboBoxItem Content="亮色"    Tag="Light" />
                        <ComboBoxItem Content="深色"    Tag="Dark" />
                    </ComboBox>
                </Grid>

                <!-- Advanced -->
                <TextBlock Text="{StaticResource Settings.SectionAdvanced}" FontWeight="SemiBold" Margin="0,8,0,0" />
                <StackPanel Margin="0,8,0,16" Orientation="Horizontal">
                    <Button Content="{StaticResource Settings.OpenLogs}" Click="OnOpenLogs" Margin="0,0,8,0" />
                    <Button Content="{StaticResource Settings.OpenDownloads}" Click="OnOpenDownloads" Margin="0,0,8,0" />
                    <Button Content="{StaticResource Settings.About}" Click="OnAbout" />
                </StackPanel>
            </StackPanel>
        </ScrollViewer>

        <StackPanel Grid.Row="1" Orientation="Horizontal" HorizontalAlignment="Right" Margin="0,16,0,0">
            <Button Content="取消" Click="OnCancel" Width="80" Margin="0,0,8,0" />
            <Button Content="儲存" Click="OnSave" Width="80" Background="{StaticResource Brush.Accent}" Foreground="White" />
        </StackPanel>
    </Grid>
</Window>
```

- [ ] **Step 2: Code-behind**

```csharp
// src/YtDlpTool/Dialogs/SettingsDialog.xaml.cs
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using YtDlpTool.Domain.Models;

namespace YtDlpTool.Dialogs;

public partial class SettingsDialog : Window
{
    private readonly AppHost _host;
    private string _selectedSaveDir;

    public SettingsDialog(AppHost host)
    {
        InitializeComponent();
        _host = host;
        _selectedSaveDir = host.Config.DefaultSaveDirectory;
        LoadFromConfig();
    }

    private void LoadFromConfig()
    {
        DefaultDirText.Text = _selectedSaveDir;
        ConcurrencySlider.Value = _host.Config.ConcurrentDownloads;
        SelectComboTag(FrequencyCombo, _host.Config.AppCheckFrequency.ToString());
        EnableYtDlpUpdates.IsChecked = _host.Config.YtDlpCheckFrequency != UpdateCheckFrequency.Never;
        EnableFfmpegUpdates.IsChecked = _host.Config.FfmpegCheckFrequency != UpdateCheckFrequency.Never;
        SelectComboTag(ThemeCombo, _host.Config.Theme.ToString());
    }

    private static void SelectComboTag(ComboBox combo, string tag)
    {
        foreach (var item in combo.Items)
            if (item is ComboBoxItem ci && (string?)ci.Tag == tag) { combo.SelectedItem = ci; return; }
        combo.SelectedIndex = 0;
    }

    private void OnBrowseDefaultDir(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "選擇預設儲存資料夾",
            InitialDirectory = Directory.Exists(_selectedSaveDir) ? _selectedSaveDir : ""
        };
        if (dlg.ShowDialog() == true)
        {
            _selectedSaveDir = dlg.FolderName;
            DefaultDirText.Text = _selectedSaveDir;
        }
    }

    private void OnOpenLogs(object sender, RoutedEventArgs e) => OpenFolder(_host.Paths.LogsDirectory);
    private void OnOpenDownloads(object sender, RoutedEventArgs e) => OpenFolder(_host.Config.DefaultSaveDirectory);
    private static void OpenFolder(string p)
    {
        try { Directory.CreateDirectory(p); Process.Start(new ProcessStartInfo("explorer.exe", p) { UseShellExecute = true }); }
        catch { }
    }

    private void OnAbout(object sender, RoutedEventArgs e)
    {
        var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        MessageBox.Show(
            $"YtDlpTool {version}\n\nSigstore 簽署者：" +
            "https://github.com/OWNER/REPO/.github/workflows/release.yml\n\n" +
            "授權：MIT (本工具)\nyt-dlp：Unlicense\nffmpeg：GPL/LGPL",
            "關於 YtDlpTool", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var cfg = _host.Config;
        cfg.DefaultSaveDirectory = _selectedSaveDir;
        cfg.ConcurrentDownloads = (int)ConcurrencySlider.Value;
        _host.Queue.MaxConcurrency = cfg.ConcurrentDownloads;
        cfg.AppCheckFrequency = ParseFrequency(FrequencyCombo);
        cfg.YtDlpCheckFrequency = EnableYtDlpUpdates.IsChecked == true ? UpdateCheckFrequency.Weekly : UpdateCheckFrequency.Never;
        cfg.FfmpegCheckFrequency = EnableFfmpegUpdates.IsChecked == true ? UpdateCheckFrequency.Monthly : UpdateCheckFrequency.Never;
        cfg.Theme = ParseTheme(ThemeCombo);
        _host.ConfigStore.Save(cfg);
        DialogResult = true;
        Close();
    }

    private static UpdateCheckFrequency ParseFrequency(ComboBox c) =>
        c.SelectedItem is ComboBoxItem it && it.Tag is string tag &&
        Enum.TryParse<UpdateCheckFrequency>(tag, out var f) ? f : UpdateCheckFrequency.Weekly;

    private static ThemePreference ParseTheme(ComboBox c) =>
        c.SelectedItem is ComboBoxItem it && it.Tag is string tag &&
        Enum.TryParse<ThemePreference>(tag, out var t) ? t : ThemePreference.System;
}
```

- [ ] **Step 3: Wire `OnSettingsClicked` in MainWindow**

Replace the placeholder in `MainWindow.xaml.cs`:

```csharp
    private void OnSettingsClicked(object sender, RoutedEventArgs e)
    {
        var host = ((App)Application.Current).Host;
        if (host is null) return;
        var dlg = new Dialogs.SettingsDialog(host) { Owner = this };
        dlg.ShowDialog();
    }
```

- [ ] **Step 4: Smoke + commit**

```powershell
dotnet run --project src/YtDlpTool/
git add src/YtDlpTool/Dialogs/SettingsDialog.xaml src/YtDlpTool/Dialogs/SettingsDialog.xaml.cs src/YtDlpTool/MainWindow.xaml.cs
git commit -m "feat(ui): SettingsDialog with downloads/updates/UI/advanced sections"
```

---

### Task 9.4: Toast notification on download complete

**Files:**
- Create: `src/YtDlpTool/Interop/ToastService.cs`
- Modify: `src/YtDlpTool/ViewModels/MainViewModel.cs` (call toast on JobCompleted)

- [ ] **Step 1: Implement `ToastService`**

```csharp
// src/YtDlpTool/Interop/ToastService.cs
using Microsoft.Toolkit.Uwp.Notifications;

namespace YtDlpTool.Interop;

public static class ToastService
{
    public static void NotifyDownloadCompleted(string title, string outputPath)
    {
        try
        {
            new ToastContentBuilder()
                .AddText("下載完成")
                .AddText(title)
                .AddText(outputPath)
                .AddButton(new ToastButton().SetContent("開啟資料夾").SetProtocolActivation(new Uri("file:///" + System.IO.Path.GetDirectoryName(outputPath)!.Replace('\\', '/'))))
                .Show();
        }
        catch { /* best effort */ }
    }

    public static void NotifyDownloadFailed(string title, string reason)
    {
        try
        {
            new ToastContentBuilder()
                .AddText("下載失敗")
                .AddText(title)
                .AddText(reason)
                .Show();
        }
        catch { }
    }
}
```

- [ ] **Step 2: Call from `MainViewModel.OnQueueEvent`**

In the `JobCompletedEvent` case, add:

```csharp
                case JobCompletedEvent e:
                    var c = Find(e.Job.Id);
                    if (c is not null)
                    {
                        c.SetStatus(JobStatus.Completed);
                        c.OutputFilePath = e.OutputFilePath;
                        c.ProgressPercent = 100;
                    }
                    Interop.ToastService.NotifyDownloadCompleted(e.Job.Title, e.OutputFilePath);
                    break;
```

And in `JobFailedEvent`:

```csharp
                case JobFailedEvent e:
                    var f = Find(e.Job.Id);
                    if (f is not null)
                    {
                        f.SetStatus(JobStatus.Failed);
                        f.FailureReason = e.Error.UserMessage;
                    }
                    Interop.ToastService.NotifyDownloadFailed(e.Job.Title, e.Error.UserMessage);
                    break;
```

- [ ] **Step 3: Smoke + commit**

```powershell
dotnet build src/YtDlpTool/
git add src/YtDlpTool/Interop/ToastService.cs src/YtDlpTool/ViewModels/MainViewModel.cs
git commit -m "feat(ui): toast notifications on download complete/failed"
```

---

### Task 9.5: Resume-on-restart prompt

**Files:**
- Modify: `src/YtDlpTool/AppHost.cs`
- Modify: `src/YtDlpTool/MainWindow.xaml.cs`

Spec 7.3 says on restart we read `state.log`, find open jobs, and prompt user. We don't actually re-download (yt-dlp would need to redo from scratch); we just surface what was interrupted so the user can re-add.

- [ ] **Step 1a: Add `ReadSnapshotAndClear` to `StateJournal`**

The same `StateJournal` instance is being written to by the queue, so we need a method that flushes/closes the current writer, reads existing events, truncates, then reopens for further appends — all atomic under the journal's lock.

Modify `src/YtDlpTool.Domain/Persistence/StateJournal.cs` and add this instance method (do not remove the existing `Append`, `ReadAll`, etc.):

```csharp
    public IReadOnlyList<StateJournalEvent> ReadSnapshotAndClear(string path)
    {
        lock (_gate)
        {
            _writer?.Flush();
            var events = ReadAll(path).ToList();
            _writer?.Dispose();
            File.WriteAllText(path, "");
            _writer = new StreamWriter(File.Open(path, FileMode.Append, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
            return events;
        }
    }
```

- [ ] **Step 1b: Add helper to `AppHost`**

Append to `AppHost` (before `Dispose`):

```csharp
    public IReadOnlyList<JobSnapshot> ReadAndClearInterruptedJobs()
    {
        var events = StateJournal.ReadSnapshotAndClear(Paths.StateLog);
        return StateJournal.ReconstructOpenJobs(events).ToList();
    }
```

- [ ] **Step 2: Prompt in MainWindow.OnLoaded**

Append to `OnLoaded`:

```csharp
        var interrupted = host.ReadAndClearInterruptedJobs();
        if (interrupted.Count > 0)
        {
            var msg = $"上次有 {interrupted.Count} 個下載尚未完成。\n\n" +
                      string.Join("\n", interrupted.Take(5).Select(j => "· " + j.Title)) +
                      (interrupted.Count > 5 ? $"\n…還有 {interrupted.Count - 5} 個" : "") +
                      "\n\n要把它們重新加回佇列嗎？";
            var result = MessageBox.Show(msg, "恢復下載", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result == MessageBoxResult.Yes)
            {
                // We don't store enough to fully reconstruct VideoFormat without metadata,
                // so we surface the URLs and let the user re-paste. v1.x compromise per spec.
                Clipboard.SetText(string.Join("\n", interrupted.Select(j => j.Url)));
                MessageBox.Show("已將未完成下載的網址複製到剪貼簿，請依序貼上重新加入。",
                    "恢復下載", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
```

- [ ] **Step 3: Smoke + commit**

```powershell
dotnet build src/YtDlpTool/
git add src/YtDlpTool/AppHost.cs src/YtDlpTool/MainWindow.xaml.cs src/YtDlpTool.Domain/Persistence/StateJournal.cs
git commit -m "feat(ui): on-restart prompt to recover interrupted downloads"
```

---

### Task 9.6: Keyboard shortcuts + first-launch hint (spec §6.6)

**Files:**
- Modify: `src/YtDlpTool/MainWindow.xaml`
- Modify: `src/YtDlpTool/MainWindow.xaml.cs`
- Modify: `src/YtDlpTool/Views/UrlInputView.xaml`
- Modify: `src/YtDlpTool/Views/UrlInputView.xaml.cs`

- [ ] **Step 1: Surface the first-launch hint in `UrlInputView`**

In `UrlInputView.xaml`, append after the `MetaCard` Border, before the closing `StackPanel`:

```xml
            <TextBlock x:Name="FirstHint"
                       Margin="0,12,0,0"
                       Foreground="{StaticResource Brush.TextTertiary}"
                       FontSize="12"
                       Visibility="Collapsed"
                       Text="{StaticResource Hint.FirstParse}" />
```

In `UrlInputView.xaml.cs`, append to `FetchMetadataAsync` immediately after `if (!vm.ShowFirstHint) vm.ShowFirstHint = true;`:

```csharp
            await ShowFirstHintBriefly();
```

Add this helper method to the class:

```csharp
    private async Task ShowFirstHintBriefly()
    {
        FirstHint.Visibility = Visibility.Visible;
        FirstHint.Opacity = 1;
        await Task.Delay(3000);
        var anim = new System.Windows.Media.Animation.DoubleAnimation
        {
            From = 1, To = 0, Duration = TimeSpan.FromMilliseconds(500)
        };
        anim.Completed += (_, _) => FirstHint.Visibility = Visibility.Collapsed;
        FirstHint.BeginAnimation(System.Windows.UIElement.OpacityProperty, anim);
    }
```

- [ ] **Step 2: Global keyboard shortcuts in `MainWindow.xaml`**

Add `InputBindings` inside `MainWindow.xaml`'s root `Window` (before the closing `</Window>`):

```xml
    <Window.InputBindings>
        <KeyBinding Modifiers="Control" Key="V" Command="{Binding PasteFromClipboardCommand, RelativeSource={RelativeSource AncestorType=Window}}" />
        <KeyBinding Key="Escape" Command="{Binding CancelSelectedQueueCommand, RelativeSource={RelativeSource AncestorType=Window}}" />
    </Window.InputBindings>
```

- [ ] **Step 3: Implement the commands in `MainWindow.xaml.cs`**

Add `using System.Windows.Input;` and these properties + methods:

```csharp
    public ICommand PasteFromClipboardCommand => new RelayCommandAdapter(_ =>
    {
        if (!Clipboard.ContainsText()) return;
        var text = Clipboard.GetText();
        UrlInput?.SetTextProgrammatically(text);
    });

    public ICommand CancelSelectedQueueCommand => new RelayCommandAdapter(_ =>
    {
        // No selectable queue item yet (Phase 8 keeps the list non-selectable).
        // Reserved for future use; currently a no-op to satisfy the keybinding.
    });

    private sealed class RelayCommandAdapter : ICommand
    {
        private readonly Action<object?> _execute;
        public RelayCommandAdapter(Action<object?> execute) => _execute = execute;
        public event EventHandler? CanExecuteChanged;
        public bool CanExecute(object? p) => true;
        public void Execute(object? p) => _execute(p);
    }
```

- [ ] **Step 4: Helper on `UrlInputView` to fill text from the command**

Add to `UrlInputView.xaml.cs`:

```csharp
    public void SetTextProgrammatically(string text)
    {
        UrlTextBox.Text = text;
        UrlTextBox.CaretIndex = text.Length;
        UrlTextBox.Focus();
    }
```

- [ ] **Step 5: Smoke**

Run the app. Press Ctrl+V while a YouTube URL is on the clipboard → URL fills into the box and metadata fetch starts.

- [ ] **Step 6: Commit**

```powershell
git add src/YtDlpTool/Views/UrlInputView.xaml src/YtDlpTool/Views/UrlInputView.xaml.cs src/YtDlpTool/MainWindow.xaml src/YtDlpTool/MainWindow.xaml.cs
git commit -m "feat(ui): Ctrl+V global paste, Esc placeholder, first-launch hint fade"
```

---

### Task 9.7: AOT publish

- [ ] **Step 1: Publish**

```powershell
dotnet publish src/YtDlpTool/YtDlpTool.csproj -c Release -r win-x64
```
Expected: succeeds.

- [ ] **Step 2: Tag**

```powershell
git tag phase-9-settings-complete
```

---

## Phase 9 complete gate

- [ ] Background update check 60s after startup with frequency policy
- [ ] `UpdateBannerView` with one-click update + progress
- [ ] `SettingsDialog` (downloads / updates / UI / advanced sections)
- [ ] `ToastService` for complete & failed
- [ ] Resume-on-restart prompt
- [ ] Ctrl+V global, first-launch hint
- [ ] AOT publish green
- [ ] Tag `phase-9-settings-complete`

Proceed to Phase 10.

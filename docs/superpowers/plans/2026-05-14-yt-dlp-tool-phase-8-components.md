# Phase 8 · UI Components (Main flow)

**Goal:** Build all main-flow UI components: `MainViewModel`, `UrlInputView` (with clipboard watch + debounce + metadata fetch), `FormatSelectorView`, `QualityDropdown`, `AdvancedOptionsView` (subtitle + clip), `SaveLocationView`, `QueuePanelView` + `QueueItemView`. Wire them into `MainWindow.FormHost`.

**Prerequisites:** Phase 7 complete (tag `phase-7-shell-complete`).

> All view-models inherit from `ObservableObject` (CommunityToolkit.Mvvm). Properties use `[ObservableProperty]` source generator. Commands use `[RelayCommand]`.

---

### Task 8.1: `MainViewModel` skeleton + DataContext wiring

**Files:**
- Create: `src/YtDlpTool/ViewModels/MainViewModel.cs`
- Modify: `src/YtDlpTool/MainWindow.xaml.cs`

- [ ] **Step 1: Create `MainViewModel`**

```csharp
// src/YtDlpTool/ViewModels/MainViewModel.cs
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using YtDlpTool.Domain.Models;

namespace YtDlpTool.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly AppHost _host;

    public MainViewModel(AppHost host)
    {
        _host = host;
        SaveDirectory = host.Config.DefaultSaveDirectory;
        Queue = new ObservableCollection<QueueItemViewModel>();
    }

    [ObservableProperty] private string _saveDirectory = "";
    [ObservableProperty] private VideoMetadata? _currentMetadata;
    [ObservableProperty] private DownloadMode _selectedMode = DownloadMode.AudioAndVideo;
    [ObservableProperty] private VideoFormat? _selectedFormat;
    [ObservableProperty] private TimeRange? _clipRange;
    [ObservableProperty] private bool _isParsing;
    [ObservableProperty] private string? _parseError;
    [ObservableProperty] private bool _showFirstHint;

    public ObservableCollection<string> SelectedSubtitleLanguages { get; } = new();
    public ObservableCollection<QueueItemViewModel> Queue { get; }

    public AppHost Host => _host;
}
```

- [ ] **Step 2: Create placeholder `QueueItemViewModel` (filled in 8.7)**

```csharp
// src/YtDlpTool/ViewModels/QueueItemViewModel.cs
using CommunityToolkit.Mvvm.ComponentModel;
using YtDlpTool.Domain.Models;

namespace YtDlpTool.ViewModels;

public partial class QueueItemViewModel : ObservableObject
{
    [ObservableProperty] private Guid _id;
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _thumbnailUrl = "";
    [ObservableProperty] private JobStatus _status;
    [ObservableProperty] private double _progressPercent;
    [ObservableProperty] private long? _bytesPerSecond;
    [ObservableProperty] private TimeSpan? _eta;
    [ObservableProperty] private string? _failureReason;
    [ObservableProperty] private string? _outputFilePath;
    [ObservableProperty] private string _modeLabel = "";
    [ObservableProperty] private string _qualityLabel = "";
}
```

- [ ] **Step 3: Wire to MainWindow**

Modify `src/YtDlpTool/MainWindow.xaml.cs`:

```csharp
// src/YtDlpTool/MainWindow.xaml.cs
using System.Windows;
using YtDlpTool.Interop;
using YtDlpTool.ViewModels;

namespace YtDlpTool;

public partial class MainWindow : Window
{
    public MainViewModel? ViewModel { get; private set; }

    public MainWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => WindowChromeHelper.ApplyAuroraBackdrop(this);
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var host = ((App)Application.Current).Host
            ?? throw new InvalidOperationException("AppHost not initialised");
        ViewModel = new MainViewModel(host);
        DataContext = ViewModel;
    }

    private void OnSettingsClicked(object sender, RoutedEventArgs e)
    {
        MessageBox.Show("設定對話框將在 Phase 9 完成", "YtDlpTool");
    }
}
```

- [ ] **Step 4: Build**

```powershell
dotnet build src/YtDlpTool/
```
Expected: succeeds.

- [ ] **Step 5: Commit**

```powershell
git add src/YtDlpTool/ViewModels/ src/YtDlpTool/MainWindow.xaml.cs
git commit -m "feat(ui): MainViewModel + QueueItemViewModel skeletons + DataContext wiring"
```

---

### Task 8.2: `UrlInputView` — clipboard detection, debounce, metadata fetch

**Files:**
- Create: `src/YtDlpTool/Views/UrlInputView.xaml`
- Create: `src/YtDlpTool/Views/UrlInputView.xaml.cs`
- Modify: `src/YtDlpTool/MainWindow.xaml` (add UrlInputView to FormHost)

- [ ] **Step 1: XAML**

```xml
<!-- src/YtDlpTool/Views/UrlInputView.xaml -->
<UserControl x:Class="YtDlpTool.Views.UrlInputView"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <Border Style="{StaticResource GlassCard}">
        <StackPanel>
            <Grid>
                <Grid.ColumnDefinitions>
                    <ColumnDefinition Width="*" />
                    <ColumnDefinition Width="Auto" />
                </Grid.ColumnDefinitions>
                <TextBox x:Name="UrlTextBox"
                         Grid.Column="0"
                         FontSize="15"
                         Background="Transparent"
                         BorderThickness="0"
                         Padding="0,6,8,6"
                         VerticalContentAlignment="Center"
                         TextChanged="OnTextChanged"
                         KeyDown="OnKeyDown" />
                <TextBlock x:Name="Placeholder"
                           Grid.Column="0"
                           Text="{StaticResource Url.Placeholder}"
                           IsHitTestVisible="False"
                           Foreground="{StaticResource Brush.TextTertiary}"
                           Padding="0,6,8,6"
                           VerticalAlignment="Center" />
                <Button x:Name="PasteButton"
                        Grid.Column="1"
                        Content="{StaticResource Url.PasteButton}"
                        Visibility="Collapsed"
                        Padding="10,4"
                        Background="Transparent"
                        Foreground="{StaticResource Brush.Accent}"
                        BorderBrush="{StaticResource Brush.Accent}"
                        BorderThickness="1"
                        Cursor="Hand"
                        Click="OnPasteClicked" />
            </Grid>

            <ProgressBar x:Name="ParsingBar"
                         Height="2" Margin="0,8,0,0"
                         IsIndeterminate="True"
                         Visibility="Collapsed" />

            <TextBlock x:Name="ErrorLabel"
                       Margin="0,8,0,0"
                       Foreground="{StaticResource Brush.Danger}"
                       Visibility="Collapsed" />

            <!-- Metadata card (shown after successful parse) -->
            <Border x:Name="MetaCard"
                    Margin="0,16,0,0"
                    Visibility="Collapsed"
                    Background="#33FFFFFF"
                    BorderBrush="{StaticResource Brush.GlassBorder}"
                    BorderThickness="1"
                    CornerRadius="10"
                    Padding="10">
                <Grid>
                    <Grid.ColumnDefinitions>
                        <ColumnDefinition Width="Auto" />
                        <ColumnDefinition Width="*" />
                        <ColumnDefinition Width="Auto" />
                    </Grid.ColumnDefinitions>
                    <Image x:Name="MetaThumb" Grid.Column="0" Width="96" Height="54" Stretch="UniformToFill" />
                    <StackPanel Grid.Column="1" Margin="12,0,0,0" VerticalAlignment="Center">
                        <TextBlock x:Name="MetaTitle"
                                   FontWeight="SemiBold" FontSize="14"
                                   Foreground="{StaticResource Brush.TextPrimary}"
                                   TextTrimming="CharacterEllipsis" />
                        <TextBlock Margin="0,4,0,0"
                                   Foreground="{StaticResource Brush.TextSecondary}">
                            <Run x:Name="MetaChannel" />
                            <Run Text=" · " />
                            <Run x:Name="MetaDuration" FontFamily="{StaticResource Font.Numeric}" />
                        </TextBlock>
                    </StackPanel>
                    <Button Grid.Column="2"
                            Content="{StaticResource Url.ClearButton}"
                            Background="Transparent" BorderThickness="0"
                            Foreground="{StaticResource Brush.TextSecondary}"
                            Cursor="Hand"
                            Click="OnClearClicked" />
                </Grid>
            </Border>
        </StackPanel>
    </Border>
</UserControl>
```

- [ ] **Step 2: Code-behind with debounce + clipboard watch**

```csharp
// src/YtDlpTool/Views/UrlInputView.xaml.cs
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using YtDlpTool.Domain.Models;
using YtDlpTool.Domain.Services;
using YtDlpTool.Resources;
using YtDlpTool.ViewModels;

namespace YtDlpTool.Views;

public partial class UrlInputView : UserControl
{
    private readonly DispatcherTimer _debounce;
    private CancellationTokenSource? _inFlightCts;
    private MainViewModel? Vm => DataContext as MainViewModel;

    public UrlInputView()
    {
        InitializeComponent();
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _debounce.Tick += OnDebounceElapsed;
        IsVisibleChanged += OnIsVisibleChanged;
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible) RefreshPasteHint();
    }

    private void OnTextChanged(object sender, TextChangedEventArgs e)
    {
        Placeholder.Visibility = string.IsNullOrEmpty(UrlTextBox.Text) ? Visibility.Visible : Visibility.Collapsed;
        ErrorLabel.Visibility = Visibility.Collapsed;
        _debounce.Stop();
        _debounce.Start();
    }

    private async void OnDebounceElapsed(object? sender, EventArgs e)
    {
        _debounce.Stop();
        var raw = UrlTextBox.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(raw))
        {
            HideMeta();
            return;
        }

        var validator = new UrlValidator();
        var result = validator.Validate(raw);
        if (!result.IsValid)
        {
            ShowError(Strings.Get("Url.InvalidNotYouTube"));
            HideMeta();
            return;
        }

        await FetchMetadataAsync(result.CanonicalUrl!);
    }

    private async Task FetchMetadataAsync(string url)
    {
        var vm = Vm; if (vm is null) return;
        _inFlightCts?.Cancel();
        _inFlightCts = new CancellationTokenSource();
        ParsingBar.Visibility = Visibility.Visible;
        vm.IsParsing = true;

        try
        {
            var result = await vm.Host.YtDlp.FetchMetadataAsync(url, _inFlightCts.Token);
            if (!result.IsSuccess || result.Metadata is null)
            {
                var mapped = vm.Host.YtDlp is null ? null : Domain.Services.ErrorMapper.Map(result.ErrorStderr ?? "");
                ShowError(mapped?.UserMessage ?? Strings.Get("Url.InvalidNotYouTube"));
                HideMeta();
                vm.CurrentMetadata = null;
                return;
            }
            vm.CurrentMetadata = result.Metadata;
            ShowMeta(result.Metadata);
            if (!vm.ShowFirstHint) vm.ShowFirstHint = true;
        }
        catch (OperationCanceledException) { /* user typed more */ }
        finally
        {
            ParsingBar.Visibility = Visibility.Collapsed;
            vm.IsParsing = false;
        }
    }

    private void ShowError(string message)
    {
        ErrorLabel.Text = message;
        ErrorLabel.Visibility = Visibility.Visible;
    }

    private void ShowMeta(VideoMetadata m)
    {
        MetaTitle.Text = m.Title;
        MetaChannel.Text = m.Channel;
        MetaDuration.Text = m.Duration.ToString(@"hh\:mm\:ss");
        if (Uri.TryCreate(m.ThumbnailUrl, UriKind.Absolute, out var thumbUri))
        {
            try { MetaThumb.Source = new BitmapImage(thumbUri); } catch { /* image load best-effort */ }
        }
        MetaCard.Visibility = Visibility.Visible;
    }

    private void HideMeta()
    {
        MetaCard.Visibility = Visibility.Collapsed;
        MetaThumb.Source = null;
    }

    private void OnPasteClicked(object sender, RoutedEventArgs e)
    {
        if (Clipboard.ContainsText())
        {
            UrlTextBox.Text = Clipboard.GetText();
            UrlTextBox.CaretIndex = UrlTextBox.Text.Length;
        }
        PasteButton.Visibility = Visibility.Collapsed;
    }

    private void OnClearClicked(object sender, RoutedEventArgs e)
    {
        UrlTextBox.Text = "";
        HideMeta();
        if (Vm is not null) Vm.CurrentMetadata = null;
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            _debounce.Stop();
            OnDebounceElapsed(this, EventArgs.Empty);
            e.Handled = true;
        }
    }

    public void RefreshPasteHint()
    {
        try
        {
            if (!Clipboard.ContainsText()) { PasteButton.Visibility = Visibility.Collapsed; return; }
            var text = Clipboard.GetText();
            var validator = new UrlValidator();
            PasteButton.Visibility = validator.Validate(text).IsValid && string.IsNullOrEmpty(UrlTextBox.Text)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
        catch { PasteButton.Visibility = Visibility.Collapsed; }
    }
}
```

- [ ] **Step 3: Add to MainWindow.xaml — replace `<StackPanel x:Name="FormHost" />`**

In `MainWindow.xaml`, replace the empty FormHost StackPanel with:

```xml
<StackPanel x:Name="FormHost">
    <views:UrlInputView x:Name="UrlInput" Margin="0,0,0,16" />
</StackPanel>
```

- [ ] **Step 4: Activate paste-hint refresh when window gains focus**

In `MainWindow.xaml.cs`, append to `OnLoaded`:

```csharp
        Activated += (_, _) => UrlInput.RefreshPasteHint();
```

- [ ] **Step 5: Manual smoke**

```powershell
dotnet run --project src/YtDlpTool/
```
Expected:
- Empty URL box with placeholder
- Type a YouTube URL → after 300ms, parse spinner appears
- If FakeYtDlp / real yt-dlp absent, error label appears (no `bin/yt-dlp.exe` yet — expected at this point)
- Click "貼上" if clipboard contains a YouTube URL → URL fills in
- Click "清除" → clears

(For full happy-path testing you need `bin/yt-dlp.exe` present — Phase 10 wires this in CI; during manual dev, copy `tests/FakeYtDlp/bin/Debug/net8.0/fake-yt-dlp.exe` → `src/YtDlpTool/bin/Debug/.../win-x64/bin/yt-dlp.exe` to smoke-test.)

- [ ] **Step 6: Commit**

```powershell
git add src/YtDlpTool/Views/UrlInputView.xaml src/YtDlpTool/Views/UrlInputView.xaml.cs src/YtDlpTool/MainWindow.xaml src/YtDlpTool/MainWindow.xaml.cs
git commit -m "feat(ui): UrlInputView with debounce, clipboard detection, metadata card"
```

---

### Task 8.3: `FormatSelectorView` (segmented control 3 modes)

**Files:**
- Create: `src/YtDlpTool/Views/FormatSelectorView.xaml`
- Create: `src/YtDlpTool/Views/FormatSelectorView.xaml.cs`
- Modify: `src/YtDlpTool/MainWindow.xaml`

- [ ] **Step 1: XAML**

```xml
<!-- src/YtDlpTool/Views/FormatSelectorView.xaml -->
<UserControl x:Class="YtDlpTool.Views.FormatSelectorView"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <Border Style="{StaticResource GlassCard}">
        <UniformGrid Columns="3" Rows="1">
            <Button x:Name="AudioBtn" Tag="AudioOnly" Click="OnModeClicked"
                    Style="{StaticResource SegmentButtonStyle}" Content="🎵 純音訊" />
            <Button x:Name="BothBtn"  Tag="AudioAndVideo" Click="OnModeClicked"
                    Style="{StaticResource SegmentButtonStyle}" Content="🎬 影音合併" />
            <Button x:Name="VideoBtn" Tag="VideoOnly" Click="OnModeClicked"
                    Style="{StaticResource SegmentButtonStyle}" Content="🎥 純影像" />
        </UniformGrid>
    </Border>
</UserControl>
```

- [ ] **Step 2: Add the `SegmentButtonStyle` to `Theme.xaml`**

Append to `src/YtDlpTool/Resources/Theme.xaml` before the closing `</ResourceDictionary>`. The style is intentionally trigger-free — selected state is toggled by code-behind manipulating `Background` and `Foreground` directly (simpler than fighting Trigger lookups).

```xml
    <Style x:Key="SegmentButtonStyle" TargetType="Button">
        <Setter Property="Background" Value="Transparent" />
        <Setter Property="Foreground" Value="{StaticResource Brush.TextSecondary}" />
        <Setter Property="FontFamily" Value="{StaticResource Font.Default}" />
        <Setter Property="FontSize" Value="14" />
        <Setter Property="Height" Value="40" />
        <Setter Property="Margin" Value="4,0" />
        <Setter Property="Cursor" Value="Hand" />
        <Setter Property="BorderThickness" Value="0" />
        <Setter Property="Template">
            <Setter.Value>
                <ControlTemplate TargetType="Button">
                    <Border x:Name="bg" Background="{TemplateBinding Background}"
                            BorderBrush="{StaticResource Brush.GlassBorder}" BorderThickness="1"
                            CornerRadius="10">
                        <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center" />
                    </Border>
                </ControlTemplate>
            </Setter.Value>
        </Setter>
    </Style>
```

- [ ] **Step 3: Code-behind**

```csharp
// src/YtDlpTool/Views/FormatSelectorView.xaml.cs
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using YtDlpTool.Domain.Models;
using YtDlpTool.ViewModels;

namespace YtDlpTool.Views;

public partial class FormatSelectorView : UserControl
{
    private MainViewModel? Vm => DataContext as MainViewModel;

    public FormatSelectorView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => RefreshSelection();
        Loaded += (_, _) => RefreshSelection();
    }

    private void OnModeClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not string tag || Vm is null) return;
        Vm.SelectedMode = Enum.Parse<DownloadMode>(tag);
        RefreshSelection();
    }

    private void RefreshSelection()
    {
        if (Vm is null) return;
        ApplyVisual(AudioBtn, Vm.SelectedMode == DownloadMode.AudioOnly);
        ApplyVisual(BothBtn,  Vm.SelectedMode == DownloadMode.AudioAndVideo);
        ApplyVisual(VideoBtn, Vm.SelectedMode == DownloadMode.VideoOnly);
    }

    private static void ApplyVisual(Button b, bool selected)
    {
        if (selected)
        {
            b.Background = (Brush)Application.Current.FindResource("Brush.Accent");
            b.Foreground = Brushes.White;
        }
        else
        {
            b.Background = Brushes.Transparent;
            b.Foreground = (Brush)Application.Current.FindResource("Brush.TextSecondary");
        }
    }
}
```

- [ ] **Step 4: Insert into MainWindow.xaml form**

In `FormHost` StackPanel, append:

```xml
    <views:FormatSelectorView Margin="0,0,0,16" />
```

- [ ] **Step 5: Manual smoke**

```powershell
dotnet run --project src/YtDlpTool/
```
Expected: three segmented buttons. Clicking changes selection (the chosen one goes accent-coloured, others go transparent).

- [ ] **Step 6: Commit**

```powershell
git add src/YtDlpTool/Views/FormatSelectorView.xaml src/YtDlpTool/Views/FormatSelectorView.xaml.cs src/YtDlpTool/Resources/Theme.xaml src/YtDlpTool/MainWindow.xaml
git commit -m "feat(ui): FormatSelectorView (segmented control 3 modes)"
```

---

### Task 8.4: `QualityDropdown`

**Files:**
- Create: `src/YtDlpTool/Views/QualityDropdown.xaml`
- Create: `src/YtDlpTool/Views/QualityDropdown.xaml.cs`
- Modify: `src/YtDlpTool/MainWindow.xaml`

`QualityDropdown` shows a list filtered by current mode + current metadata. It's just a `ComboBox` styled to match — fancy glass popups can come later if needed.

- [ ] **Step 1: XAML**

```xml
<!-- src/YtDlpTool/Views/QualityDropdown.xaml -->
<UserControl x:Class="YtDlpTool.Views.QualityDropdown"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <Border Style="{StaticResource GlassCard}" Padding="16,12">
        <StackPanel>
            <TextBlock Text="{StaticResource Quality.Label}"
                       FontSize="12"
                       Foreground="{StaticResource Brush.TextSecondary}"
                       Margin="0,0,0,4" />
            <ComboBox x:Name="QualityCombo" Height="36"
                      SelectionChanged="OnSelectionChanged"
                      DisplayMemberPath="Label" />
        </StackPanel>
    </Border>
</UserControl>
```

- [ ] **Step 2: Code-behind**

```csharp
// src/YtDlpTool/Views/QualityDropdown.xaml.cs
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using YtDlpTool.Domain.Models;
using YtDlpTool.ViewModels;

namespace YtDlpTool.Views;

public partial class QualityDropdown : UserControl
{
    public sealed record QualityOption(string Label, VideoFormat Format);

    private MainViewModel? Vm => DataContext as MainViewModel;

    public QualityDropdown()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is MainViewModel oldVm) oldVm.PropertyChanged -= OnVmChanged;
        if (e.NewValue is MainViewModel newVm) newVm.PropertyChanged += OnVmChanged;
        Rebuild();
    }

    private void OnVmChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.CurrentMetadata) or nameof(MainViewModel.SelectedMode))
            Rebuild();
    }

    private void Rebuild()
    {
        if (Vm is null || Vm.CurrentMetadata is null) { QualityCombo.ItemsSource = null; return; }
        var options = BuildOptions(Vm.SelectedMode, Vm.CurrentMetadata.Formats);
        QualityCombo.ItemsSource = new ObservableCollection<QualityOption>(options);
        QualityCombo.SelectedIndex = 0;
    }

    private static IEnumerable<QualityOption> BuildOptions(DownloadMode mode, IReadOnlyList<VideoFormat> formats)
    {
        switch (mode)
        {
            case DownloadMode.AudioOnly:
                foreach (var f in formats.Where(f => f.AudioCodec is not null && f.VideoCodec is null)
                                         .OrderByDescending(f => f.AudioBitrateKbps ?? 0)
                                         .Take(2))
                    yield return new QualityOption(LabelAudio(f), f);
                break;
            case DownloadMode.VideoOnly:
            case DownloadMode.AudioAndVideo:
                foreach (var f in formats.Where(f => f.VideoCodec is not null)
                                         .GroupBy(f => f.Height ?? 0)
                                         .OrderByDescending(g => g.Key)
                                         .Take(3)
                                         .Select(g => g.OrderByDescending(x => x.FileSizeBytes ?? 0).First()))
                    yield return new QualityOption(LabelVideo(f), f);
                break;
        }
    }

    private static string LabelAudio(VideoFormat f)
    {
        var bps = f.AudioBitrateKbps is { } b ? $"{b}k" : "?";
        var size = FormatSize(f.FileSizeBytes);
        return $"{f.Extension.ToUpper()} · {bps} · ~{size}";
    }

    private static string LabelVideo(VideoFormat f)
    {
        var height = f.Height is { } h ? $"{h}p" : "?";
        var codec = f.VideoCodec ?? "";
        var size = FormatSize(f.FileSizeBytes);
        return $"{height} · ~{size} · {codec}";
    }

    private static string FormatSize(long? bytes)
    {
        if (bytes is null) return "?";
        double v = bytes.Value;
        string u = "B";
        if (v >= 1024) { v /= 1024; u = "KB"; }
        if (v >= 1024) { v /= 1024; u = "MB"; }
        if (v >= 1024) { v /= 1024; u = "GB"; }
        return $"{v:0.#} {u}";
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Vm is null) return;
        if (QualityCombo.SelectedItem is QualityOption opt) Vm.SelectedFormat = opt.Format;
    }
}
```

- [ ] **Step 3: Add to MainWindow**

In `FormHost` after FormatSelector:

```xml
    <views:QualityDropdown Margin="0,0,0,16" />
```

- [ ] **Step 4: Manual smoke**

Run app. After URL parses (with fake binary), select a mode — dropdown should populate with options.

- [ ] **Step 5: Commit**

```powershell
git add src/YtDlpTool/Views/QualityDropdown.xaml src/YtDlpTool/Views/QualityDropdown.xaml.cs src/YtDlpTool/MainWindow.xaml
git commit -m "feat(ui): QualityDropdown derives options from mode + metadata"
```

---

### Task 8.5: `AdvancedOptionsView` (subtitles + clip)

**Files:**
- Create: `src/YtDlpTool/Views/AdvancedOptionsView.xaml`
- Create: `src/YtDlpTool/Views/AdvancedOptionsView.xaml.cs`
- Modify: `src/YtDlpTool/MainWindow.xaml`

- [ ] **Step 1: XAML**

```xml
<!-- src/YtDlpTool/Views/AdvancedOptionsView.xaml -->
<UserControl x:Class="YtDlpTool.Views.AdvancedOptionsView"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <Border Style="{StaticResource GlassCard}" Padding="16,12">
        <Expander x:Name="ExpanderRoot" Background="Transparent" BorderThickness="0">
            <Expander.Header>
                <StackPanel Orientation="Horizontal">
                    <TextBlock Text="{StaticResource Advanced.Title}" FontWeight="SemiBold" />
                    <TextBlock Margin="8,0,0,0"
                               Foreground="{StaticResource Brush.TextTertiary}"
                               Text="{StaticResource Advanced.Hint}" />
                </StackPanel>
            </Expander.Header>
            <StackPanel Margin="0,12,0,0">
                <!-- Subtitles -->
                <TextBlock Text="{StaticResource Advanced.SubtitlesLabel}"
                           FontSize="12"
                           Foreground="{StaticResource Brush.TextSecondary}"
                           Margin="0,0,0,4" />
                <ItemsControl x:Name="SubtitlesList">
                    <ItemsControl.ItemsPanel>
                        <ItemsPanelTemplate>
                            <WrapPanel />
                        </ItemsPanelTemplate>
                    </ItemsControl.ItemsPanel>
                </ItemsControl>

                <!-- Clip -->
                <TextBlock Text="{StaticResource Advanced.ClipLabel}"
                           FontSize="12"
                           Foreground="{StaticResource Brush.TextSecondary}"
                           Margin="0,16,0,4" />
                <StackPanel Orientation="Horizontal">
                    <CheckBox x:Name="ClipEnabled" Content="{StaticResource Advanced.ClipLabel}"
                              Checked="OnClipToggle" Unchecked="OnClipToggle" />
                    <TextBox x:Name="ClipStart" Width="84" Margin="12,0,0,0" Text="00:00:00"
                             TextChanged="OnClipChanged" IsEnabled="False" />
                    <TextBlock Text="—" Margin="4,0" VerticalAlignment="Center" />
                    <TextBox x:Name="ClipEnd" Width="84" Text="00:01:00"
                             TextChanged="OnClipChanged" IsEnabled="False" />
                </StackPanel>
                <TextBlock x:Name="ClipError"
                           Margin="0,4,0,0"
                           Foreground="{StaticResource Brush.Danger}"
                           Visibility="Collapsed" />
            </StackPanel>
        </Expander>
    </Border>
</UserControl>
```

- [ ] **Step 2: Code-behind**

```csharp
// src/YtDlpTool/Views/AdvancedOptionsView.xaml.cs
using System.Windows;
using System.Windows.Controls;
using YtDlpTool.Domain.Models;
using YtDlpTool.Domain.Services;
using YtDlpTool.Resources;
using YtDlpTool.ViewModels;

namespace YtDlpTool.Views;

public partial class AdvancedOptionsView : UserControl
{
    private const int MaxSubtitles = 3;
    private MainViewModel? Vm => DataContext as MainViewModel;

    public AdvancedOptionsView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is MainViewModel oldVm) oldVm.PropertyChanged -= OnVmChanged;
        if (e.NewValue is MainViewModel newVm) newVm.PropertyChanged += OnVmChanged;
        RebuildSubtitles();
    }

    private void OnVmChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.CurrentMetadata)) RebuildSubtitles();
    }

    private void RebuildSubtitles()
    {
        if (Vm is null || Vm.CurrentMetadata is null)
        {
            SubtitlesList.ItemsSource = Array.Empty<object>();
            return;
        }
        Vm.SelectedSubtitleLanguages.Clear();
        var items = Vm.CurrentMetadata.Subtitles.Select(s => new CheckBox
        {
            Content = s.DisplayName + (s.IsAutoGenerated ? Strings.Get("Advanced.SubtitlesAutoSuffix") : ""),
            Tag = s.LanguageCode,
            Margin = new Thickness(0, 0, 8, 4)
        }).ToList();
        foreach (var c in items)
        {
            c.Checked += OnSubtitleToggle;
            c.Unchecked += OnSubtitleToggle;
        }
        SubtitlesList.ItemsSource = items;
    }

    private void OnSubtitleToggle(object sender, RoutedEventArgs e)
    {
        if (Vm is null) return;
        Vm.SelectedSubtitleLanguages.Clear();
        var checkedCount = 0;
        foreach (var item in SubtitlesList.Items)
        {
            if (item is CheckBox c && c.IsChecked == true && c.Tag is string lang)
            {
                if (checkedCount >= MaxSubtitles)
                {
                    c.IsChecked = false;
                    continue;
                }
                Vm.SelectedSubtitleLanguages.Add(lang);
                checkedCount++;
            }
        }
    }

    private void OnClipToggle(object sender, RoutedEventArgs e)
    {
        var enabled = ClipEnabled.IsChecked == true;
        ClipStart.IsEnabled = enabled;
        ClipEnd.IsEnabled = enabled;
        if (!enabled && Vm is not null) Vm.ClipRange = null;
        if (enabled) ApplyClip();
    }

    private void OnClipChanged(object sender, TextChangedEventArgs e)
    {
        if (ClipEnabled.IsChecked == true) ApplyClip();
    }

    private void ApplyClip()
    {
        if (Vm is null || Vm.CurrentMetadata is null) return;
        var r = TimeRangeValidator.Parse(ClipStart.Text, ClipEnd.Text, Vm.CurrentMetadata.Duration);
        if (!r.IsValid)
        {
            ClipError.Text = r.Reason!;
            ClipError.Visibility = Visibility.Visible;
            Vm.ClipRange = null;
            return;
        }
        ClipError.Visibility = Visibility.Collapsed;
        Vm.ClipRange = r.Range;
    }
}
```

- [ ] **Step 3: Insert in MainWindow**

```xml
    <views:AdvancedOptionsView Margin="0,0,0,16" />
```

- [ ] **Step 4: Smoke**

Run, expand the section, check subtitles list populates after URL parsing, toggle clip checkbox enables fields.

- [ ] **Step 5: Commit**

```powershell
git add src/YtDlpTool/Views/AdvancedOptionsView.xaml src/YtDlpTool/Views/AdvancedOptionsView.xaml.cs src/YtDlpTool/MainWindow.xaml
git commit -m "feat(ui): AdvancedOptionsView (subtitles up to 3, clip with validation)"
```

---

### Task 8.6: `SaveLocationView`

**Files:**
- Create: `src/YtDlpTool/Views/SaveLocationView.xaml`
- Create: `src/YtDlpTool/Views/SaveLocationView.xaml.cs`
- Modify: `src/YtDlpTool/MainWindow.xaml`

- [ ] **Step 1: XAML**

```xml
<!-- src/YtDlpTool/Views/SaveLocationView.xaml -->
<UserControl x:Class="YtDlpTool.Views.SaveLocationView"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <Border Style="{StaticResource GlassCard}" Padding="16,10">
        <Grid>
            <Grid.ColumnDefinitions>
                <ColumnDefinition Width="Auto" />
                <ColumnDefinition Width="*" />
                <ColumnDefinition Width="Auto" />
            </Grid.ColumnDefinitions>
            <TextBlock Grid.Column="0" Text="{StaticResource Save.Label}"
                       Foreground="{StaticResource Brush.TextSecondary}"
                       FontSize="12" VerticalAlignment="Center" />
            <TextBlock Grid.Column="1" x:Name="PathLabel"
                       Margin="12,0,0,0"
                       TextTrimming="CharacterEllipsis"
                       VerticalAlignment="Center"
                       Foreground="{StaticResource Brush.TextPrimary}"
                       ToolTip="{Binding ElementName=PathLabel, Path=Text}" />
            <Button Grid.Column="2"
                    Content="{StaticResource Save.Browse}"
                    Background="Transparent" BorderThickness="0"
                    Foreground="{StaticResource Brush.Accent}"
                    Cursor="Hand"
                    Click="OnBrowseClicked" />
        </Grid>
    </Border>
</UserControl>
```

- [ ] **Step 2: Code-behind**

```csharp
// src/YtDlpTool/Views/SaveLocationView.xaml.cs
using System.Windows;
using System.Windows.Controls;
using YtDlpTool.ViewModels;

namespace YtDlpTool.Views;

public partial class SaveLocationView : UserControl
{
    private MainViewModel? Vm => DataContext as MainViewModel;

    public SaveLocationView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is MainViewModel oldVm) oldVm.PropertyChanged -= OnVmChanged;
        if (e.NewValue is MainViewModel newVm)
        {
            newVm.PropertyChanged += OnVmChanged;
            PathLabel.Text = newVm.SaveDirectory;
        }
    }

    private void OnVmChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.SaveDirectory) && Vm is not null)
            PathLabel.Text = Vm.SaveDirectory;
    }

    private void OnBrowseClicked(object sender, RoutedEventArgs e)
    {
        if (Vm is null) return;
        using var dlg = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "選擇下載資料夾",
            UseDescriptionForTitle = true,
            SelectedPath = Directory.Exists(Vm.SaveDirectory) ? Vm.SaveDirectory : ""
        };
        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK && !string.IsNullOrEmpty(dlg.SelectedPath))
        {
            Vm.SaveDirectory = dlg.SelectedPath;
            Vm.Host.Config.DefaultSaveDirectory = dlg.SelectedPath;
            Vm.Host.ConfigStore.Save(Vm.Host.Config);
        }
    }
}
```

- [ ] **Step 3: Add to MainWindow**

```xml
    <views:SaveLocationView Margin="0,0,0,16" />
```

- [ ] **Step 4: Smoke + commit**

```powershell
dotnet run --project src/YtDlpTool/
git add src/YtDlpTool/Views/SaveLocationView.xaml src/YtDlpTool/Views/SaveLocationView.xaml.cs src/YtDlpTool/MainWindow.xaml
git commit -m "feat(ui): SaveLocationView with folder picker and config persistence"
```

---

### Task 8.7: Primary action button + queue panel

**Files:**
- Create: `src/YtDlpTool/Views/QueuePanelView.xaml`
- Create: `src/YtDlpTool/Views/QueuePanelView.xaml.cs`
- Create: `src/YtDlpTool/Views/QueueItemTemplate.xaml` (template only)
- Modify: `src/YtDlpTool/ViewModels/MainViewModel.cs` (add commands)
- Modify: `src/YtDlpTool/MainWindow.xaml`

- [ ] **Step 1: Add commands to `MainViewModel`**

Replace `src/YtDlpTool/ViewModels/MainViewModel.cs`:

```csharp
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using YtDlpTool.Domain.Models;
using YtDlpTool.Domain.Services;

namespace YtDlpTool.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly AppHost _host;

    public MainViewModel(AppHost host)
    {
        _host = host;
        SaveDirectory = host.Config.DefaultSaveDirectory;
        Queue = new ObservableCollection<QueueItemViewModel>();
        host.QueueEventRaised += OnQueueEvent;
    }

    [ObservableProperty] private string _saveDirectory = "";
    [ObservableProperty] private VideoMetadata? _currentMetadata;
    [ObservableProperty] private DownloadMode _selectedMode = DownloadMode.AudioAndVideo;
    [ObservableProperty] private VideoFormat? _selectedFormat;
    [ObservableProperty] private TimeRange? _clipRange;
    [ObservableProperty] private bool _isParsing;
    [ObservableProperty] private string? _parseError;
    [ObservableProperty] private bool _showFirstHint;

    public ObservableCollection<string> SelectedSubtitleLanguages { get; } = new();
    public ObservableCollection<QueueItemViewModel> Queue { get; }
    public AppHost Host => _host;

    public bool CanAddDownload =>
        CurrentMetadata is not null && SelectedFormat is not null && !string.IsNullOrEmpty(SaveDirectory);

    [RelayCommand]
    private void AddDownload()
    {
        if (!CanAddDownload || CurrentMetadata is null || SelectedFormat is null) return;
        var job = new DownloadJob(
            url: $"https://www.youtube.com/watch?v={CurrentMetadata.VideoId}",
            title: CurrentMetadata.Title,
            thumbnailUrl: CurrentMetadata.ThumbnailUrl,
            mode: SelectedMode,
            chosenFormat: SelectedFormat,
            subtitleLanguageCodes: SelectedSubtitleLanguages.ToArray(),
            clipRange: ClipRange,
            saveDirectory: SaveDirectory);
        _host.Queue.Enqueue(job);
        CurrentMetadata = null;
        SelectedFormat = null;
        ClipRange = null;
        SelectedSubtitleLanguages.Clear();
    }

    [RelayCommand]
    private void CancelJob(Guid id) => _host.Queue.Cancel(id);

    private void OnQueueEvent(object? sender, QueueEvent evt)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null) return;
        dispatcher.Invoke(() =>
        {
            switch (evt)
            {
                case JobEnqueuedEvent e:
                    Queue.Add(new QueueItemViewModel
                    {
                        Id = e.Job.Id,
                        Title = e.Job.Title,
                        ThumbnailUrl = e.Job.ThumbnailUrl,
                        Status = JobStatus.Pending,
                        ModeLabel = ModeLabel(e.Job.Mode),
                        QualityLabel = QualityLabel(e.Job.ChosenFormat)
                    });
                    break;
                case JobStartedEvent e:
                    Find(e.Job.Id)?.SetStatus(JobStatus.Downloading);
                    break;
                case JobProgressEvent e:
                    var vm = Find(e.Job.Id);
                    if (vm is not null)
                    {
                        vm.ProgressPercent = e.Progress.Percent;
                        vm.BytesPerSecond = e.Progress.BytesPerSecond;
                        vm.Eta = e.Progress.Eta;
                    }
                    break;
                case JobCompletedEvent e:
                    var c = Find(e.Job.Id);
                    if (c is not null)
                    {
                        c.SetStatus(JobStatus.Completed);
                        c.OutputFilePath = e.OutputFilePath;
                        c.ProgressPercent = 100;
                    }
                    break;
                case JobFailedEvent e:
                    var f = Find(e.Job.Id);
                    if (f is not null)
                    {
                        f.SetStatus(JobStatus.Failed);
                        f.FailureReason = e.Error.UserMessage;
                    }
                    break;
                case JobCancelledEvent e:
                    var x = Find(e.Job.Id);
                    if (x is not null) x.SetStatus(JobStatus.Cancelled);
                    break;
            }
        });
    }

    private QueueItemViewModel? Find(Guid id) => Queue.FirstOrDefault(q => q.Id == id);

    private static string ModeLabel(DownloadMode m) => m switch
    {
        DownloadMode.AudioOnly => "音訊",
        DownloadMode.VideoOnly => "影像",
        DownloadMode.AudioAndVideo => "影音",
        _ => ""
    };

    private static string QualityLabel(VideoFormat f) =>
        f.Height is { } h ? $"{h}p" : f.AudioBitrateKbps is { } k ? $"{k}kbps" : f.FormatId;
}
```

- [ ] **Step 2: Update `QueueItemViewModel` with helper**

Append to `src/YtDlpTool/ViewModels/QueueItemViewModel.cs`:

```csharp
    public void SetStatus(JobStatus s) => Status = s;
```

- [ ] **Step 3: Queue panel XAML**

```xml
<!-- src/YtDlpTool/Views/QueuePanelView.xaml -->
<UserControl x:Class="YtDlpTool.Views.QueuePanelView"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:vm="clr-namespace:YtDlpTool.ViewModels">
    <Border Style="{StaticResource GlassCard}" Padding="16,12">
        <StackPanel>
            <TextBlock>
                <Run Text="{StaticResource Queue.Title}" />
                <Run x:Name="CountRun" Text=" (0)"
                     Foreground="{StaticResource Brush.TextTertiary}" />
            </TextBlock>
            <ItemsControl x:Name="QueueItems" Margin="0,12,0,0"
                          ItemsSource="{Binding Queue}">
                <ItemsControl.ItemTemplate>
                    <DataTemplate DataType="{x:Type vm:QueueItemViewModel}">
                        <Border Margin="0,0,0,8"
                                BorderBrush="{StaticResource Brush.GlassBorder}"
                                BorderThickness="0,0,0,1"
                                Padding="0,8">
                            <Grid>
                                <Grid.ColumnDefinitions>
                                    <ColumnDefinition Width="3" />
                                    <ColumnDefinition Width="Auto" />
                                    <ColumnDefinition Width="*" />
                                    <ColumnDefinition Width="Auto" />
                                </Grid.ColumnDefinitions>
                                <Rectangle Grid.Column="0" Fill="{Binding Status, Converter={StaticResource StatusToColorConverter}}"
                                           Width="3" />
                                <Image Grid.Column="1" Width="64" Height="36" Margin="8,0"
                                       Stretch="UniformToFill"
                                       Source="{Binding ThumbnailUrl}" />
                                <StackPanel Grid.Column="2" VerticalAlignment="Center">
                                    <TextBlock Text="{Binding Title}" TextTrimming="CharacterEllipsis"
                                               Foreground="{StaticResource Brush.TextPrimary}" />
                                    <ProgressBar Margin="0,4,0,0" Height="4"
                                                 Value="{Binding ProgressPercent}" />
                                    <TextBlock FontSize="11" Margin="0,4,0,0"
                                               Foreground="{StaticResource Brush.TextSecondary}"
                                               FontFamily="{StaticResource Font.Numeric}"
                                               Text="{Binding Converter={StaticResource QueueMetaConverter}}" />
                                </StackPanel>
                                <Button Grid.Column="3"
                                        Background="Transparent" BorderThickness="0"
                                        Content="✕"
                                        FontSize="14"
                                        Foreground="{StaticResource Brush.TextTertiary}"
                                        Cursor="Hand"
                                        Margin="8,0,0,0"
                                        Tag="{Binding Id}"
                                        Click="OnCancelClicked" />
                            </Grid>
                        </Border>
                    </DataTemplate>
                </ItemsControl.ItemTemplate>
            </ItemsControl>
        </StackPanel>
    </Border>
</UserControl>
```

- [ ] **Step 4: Converters used in the template**

```csharp
// src/YtDlpTool/Views/Converters/StatusToColorConverter.cs
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using YtDlpTool.Domain.Models;

namespace YtDlpTool.Views.Converters;

public sealed class StatusToColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not JobStatus s) return Brushes.Transparent;
        return s switch
        {
            JobStatus.Pending     => (Brush)Application.Current.FindResource("Brush.TextTertiary"),
            JobStatus.Downloading => (Brush)Application.Current.FindResource("Brush.Accent"),
            JobStatus.Completed   => (Brush)Application.Current.FindResource("Brush.Success"),
            JobStatus.Failed      => (Brush)Application.Current.FindResource("Brush.Danger"),
            _                     => Brushes.Gray
        };
    }
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => throw new NotImplementedException();
}
```

```csharp
// src/YtDlpTool/Views/Converters/QueueMetaConverter.cs
using System.Globalization;
using System.Windows.Data;
using YtDlpTool.ViewModels;

namespace YtDlpTool.Views.Converters;

public sealed class QueueMetaConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not QueueItemViewModel v) return "";
        var pct = $"{v.ProgressPercent:0.#}%";
        var speed = v.BytesPerSecond is { } b ? $" · {FormatSpeed(b)}" : "";
        var eta = v.Eta is { } t ? $" · 剩餘 {t:hh\\:mm\\:ss}" : "";
        var mode = $" · {v.QualityLabel} {v.ModeLabel}";
        var failure = v.FailureReason is { } r ? $"  ⚠ {r}" : "";
        return pct + speed + eta + mode + failure;
    }
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => throw new NotImplementedException();
    private static string FormatSpeed(long b)
    {
        double v = b; var u = "B/s";
        if (v >= 1024) { v /= 1024; u = "KB/s"; }
        if (v >= 1024) { v /= 1024; u = "MB/s"; }
        return $"{v:0.#} {u}";
    }
}
```

- [ ] **Step 5: Register converters in `App.xaml`**

Replace the entire `App.xaml` with (note `xmlns:conv` is declared at the `Application` root for clean XAML):

```xml
<Application x:Class="YtDlpTool.App"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:conv="clr-namespace:YtDlpTool.Views.Converters"
             StartupUri="MainWindow.xaml">
    <Application.Resources>
        <ResourceDictionary>
            <ResourceDictionary.MergedDictionaries>
                <ResourceDictionary Source="/Resources/Strings.zh-TW.xaml" />
                <ResourceDictionary Source="/Resources/Theme.xaml" />
            </ResourceDictionary.MergedDictionaries>
            <conv:StatusToColorConverter x:Key="StatusToColorConverter" />
            <conv:QueueMetaConverter x:Key="QueueMetaConverter" />
        </ResourceDictionary>
    </Application.Resources>
</Application>
```

- [ ] **Step 6: Queue panel code-behind**

```csharp
// src/YtDlpTool/Views/QueuePanelView.xaml.cs
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using YtDlpTool.ViewModels;

namespace YtDlpTool.Views;

public partial class QueuePanelView : UserControl
{
    public QueuePanelView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is MainViewModel vm)
        {
            vm.Queue.CollectionChanged += OnCollectionChanged;
            UpdateCount(vm.Queue.Count);
        }
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (DataContext is MainViewModel vm) UpdateCount(vm.Queue.Count);
    }

    private void UpdateCount(int n) => CountRun.Text = $" ({n})";

    private void OnCancelClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is Guid id && DataContext is MainViewModel vm)
            vm.CancelJobCommand.Execute(id);
    }
}
```

- [ ] **Step 7: Add primary button + QueuePanelView to MainWindow**

In `FormHost`, after SaveLocationView, append:

```xml
    <Button x:Name="AddDownloadButton"
            Style="{StaticResource PrimaryButton}"
            Content="{StaticResource Button.AddDownload}"
            Margin="0,0,0,16"
            Click="OnAddDownloadClicked" />

    <views:QueuePanelView />
```

In `MainWindow.xaml.cs` add:

```csharp
    private void OnAddDownloadClicked(object sender, RoutedEventArgs e)
    {
        ViewModel?.AddDownloadCommand.Execute(null);
    }
```

- [ ] **Step 8: Smoke**

```powershell
dotnet run --project src/YtDlpTool/
```
Expected: paste URL → metadata appears → format selected → click 加入下載 → queue item appears below. (Real download will work only after CI bundles real `yt-dlp.exe` + `ffmpeg.exe` in `bin/`.)

- [ ] **Step 9: Commit**

```powershell
git add src/YtDlpTool/Views/ src/YtDlpTool/ViewModels/MainViewModel.cs src/YtDlpTool/ViewModels/QueueItemViewModel.cs src/YtDlpTool/MainWindow.xaml src/YtDlpTool/MainWindow.xaml.cs src/YtDlpTool/App.xaml
git commit -m "feat(ui): primary button + QueuePanelView with progress, status colour, cancel"
```

---

### Task 8.8: AOT publish

- [ ] **Step 1: Publish**

```powershell
dotnet publish src/YtDlpTool/YtDlpTool.csproj -c Release -r win-x64
```
Expected: succeeds. Watch for `IL2026`/`IL3050` from value converters or binding — fix by ensuring all bound types are in the AOT context.

- [ ] **Step 2: Tag**

```powershell
git tag phase-8-components-complete
```

---

## Phase 8 complete gate

- [ ] `MainViewModel` with commands + queue events
- [ ] `UrlInputView`, `FormatSelectorView`, `QualityDropdown`, `AdvancedOptionsView`, `SaveLocationView`, `QueuePanelView`
- [ ] All wired into MainWindow's FormHost
- [ ] Manual: paste URL → metadata → choose format → click "加入下載" → queue item appears
- [ ] AOT publish green
- [ ] Tag `phase-8-components-complete`

Proceed to Phase 9.

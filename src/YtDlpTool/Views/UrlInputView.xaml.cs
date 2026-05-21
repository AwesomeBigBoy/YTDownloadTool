using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using YtDlpTool.Domain.Logging;
using YtDlpTool.Domain.Models;
using YtDlpTool.Domain.Services;
using YtDlpTool.Interop;
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

        var urlHash = AppLogger.HashSuffix(url);
        var sw = Stopwatch.StartNew();
        vm.Host.Logger.Info("url.parse.started", new Dictionary<string, string>
        {
            ["url_hash"] = urlHash
        });

        try
        {
            var result = await vm.Host.YtDlp.FetchMetadataAsync(url, _inFlightCts.Token);
            if (!result.IsSuccess || result.Metadata is null)
            {
                var mapped = vm.Host.YtDlp is null ? null : Domain.Services.ErrorMapper.Map(result.ErrorStderr ?? "");
                ShowError(mapped?.UserMessage ?? Strings.Get("Url.InvalidNotYouTube"));
                HideMeta();
                vm.CurrentMetadata = null;
                vm.Host.Logger.Warn("url.parse.failed", new Dictionary<string, string>
                {
                    ["url_hash"] = urlHash,
                    ["category"] = mapped?.Category.ToString() ?? "Unknown",
                    ["error_code"] = mapped?.ErrorCode ?? "E-UNKNOWN",
                    ["elapsed_ms"] = ((int)sw.ElapsedMilliseconds).ToString(),
                    ["details"] = TruncateForLog(result.ErrorStderr ?? "")
                });
                // v1.3.0/1.3.3: only offer the SSL bypass when the failure looks
                // SSL-related. ShouldOfferSslPrompt checks both the code and the
                // raw stderr, so zero-output timeouts (yt-dlp launch failure) don't
                // get mis-routed into "is this SSL?".
                if (mapped is not null && ViewModels.MainViewModel.ShouldOfferSslPrompt(mapped))
                {
                    App.OfferSslBypassPrompt(vm.Host,
                        "貼上 YouTube 網址後解析失敗：" + mapped.UserMessage);
                }
                return;
            }
            vm.CurrentMetadata = result.Metadata;
            ShowMeta(result.Metadata);
            if (!vm.ShowFirstHint) vm.ShowFirstHint = true;
            vm.Host.Logger.Info("url.parse.completed", new Dictionary<string, string>
            {
                ["url_hash"] = urlHash,
                ["video_id"] = result.Metadata.VideoId,
                ["formats_count"] = result.Metadata.Formats.Count.ToString(),
                ["elapsed_ms"] = ((int)sw.ElapsedMilliseconds).ToString()
            });
            await ShowFirstHintBriefly();
        }
        catch (OperationCanceledException) { /* user typed more */ }
        finally
        {
            ParsingBar.Visibility = Visibility.Collapsed;
            vm.IsParsing = false;
        }
    }

    /// <summary>
    /// Collapses newlines to " | " and clips to 500 chars so a noisy yt-dlp stderr
    /// fits cleanly on one log line. Used only for the operational log; raw stderr
    /// stays in MappedError.RawDetails for diagnostic chaining.
    /// </summary>
    private static string TruncateForLog(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var collapsed = s.Replace("\r\n", " | ").Replace('\n', '|').Replace('\r', '|').Trim();
        if (collapsed.Length > 500) collapsed = collapsed.Substring(0, 500);
        return collapsed;
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
        // Route the thumbnail through InMemoryThumbnailLoader so we never write a copy
        // into WinINet's cache. The fire-and-forget Task is OK here: if it succeeds we
        // poke the Image source; if it fails we silently leave the placeholder.
        _ = LoadMetaThumbAsync(m.ThumbnailUrl);
        MetaCard.Visibility = Visibility.Visible;
    }

    private async Task LoadMetaThumbAsync(string url)
    {
        var bmp = await InMemoryThumbnailLoader.LoadAsync(url).ConfigureAwait(true);
        if (bmp is not null) MetaThumb.Source = bmp;
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

    public void SetTextProgrammatically(string text)
    {
        UrlTextBox.Text = text;
        UrlTextBox.CaretIndex = text.Length;
        UrlTextBox.Focus();
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

using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using YtDlpTool.Dialogs;
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
    private MainWindow? Main => Window.GetWindow(this) as MainWindow;

    public UrlInputView()
    {
        InitializeComponent();
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _debounce.Tick += OnDebounceElapsed;
        // v1.5.0: Ctrl+V into the box goes through the same handling as 貼上 and drops.
        DataObject.AddPastingHandler(UrlTextBox, OnPasting);
    }

    // v1.5.0: several links, a playlist, or one link inside other text (a chat message, a
    // whole e-mail) are taken apart instead of pasted verbatim. Text without any YouTube
    // link pastes as typed, and the box then says what is wrong with it.
    private void OnPasting(object sender, DataObjectPastingEventArgs e)
    {
        if (Main is not { } main) return;
        string? text;
        try
        {
            text = e.SourceDataObject.GetData(DataFormats.UnicodeText, true) as string;
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            return;   // clipboard held by another app: let the TextBox try on its own
        }
        if (text is null || LinkExtractor.Extract(text).Count == 0) return;
        e.CancelCommand();
        // After the paste command has unwound: the handler may replace the box's text or
        // open a modal dialog, neither of which belongs inside the TextBox's own paste.
        Dispatcher.BeginInvoke(() => main.HandleIncomingText(text, fromDrop: false));
    }

    /// <summary>v1.5.0: a message under the box (a playlist that could not be read, a drop with no link).</summary>
    public void ShowNotice(string message) => ShowError(message);

    // v1.5.1: asked once per session; a 「否」 leaves the banner's 修復 in place.
    private bool _repairOffered;

    /// <summary>
    /// v1.5.1 (report 2026-11-12): a parse or playlist read failed because yt-dlp cannot
    /// start. People pasted a link seconds after opening, read the message under the box and
    /// never pressed the banner's 修復 — so ask right here, and parse the link again once it
    /// is fixed. The question waits until the failed request has unwound.
    /// </summary>
    public void OfferRepair()
    {
        if (Vm is not { } vm) return;
        vm.Host.ShowYtDlpUnavailable();
        if (_repairOffered) return;
        _repairOffered = true;
        Dispatcher.BeginInvoke(new Action(async () =>
        {
            var owner = Window.GetWindow(this);
            var answer = AppDialog.Show(owner,
                "yt-dlp 元件無法啟動，所以無法解析這個網址。\n\n" +
                "要現在修復嗎？程式會重新下載並驗證 yt-dlp 元件（約 24 MB），完成後立即換上，不必重新開啟程式。",
                "需要修復元件", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes) return;
            if (!await YtDlpRepairFlow.RunAsync(vm.Host, owner)) return;
            // The link is still in the box: read it again now that yt-dlp answers.
            _debounce.Stop();
            _debounce.Start();
        }));
    }

    /// <summary>
    /// v1.5.0: read a playlist's videos. It owns the 「正在讀取…」 strip and the in-flight
    /// token exactly as a parse does, so a newer paste, another playlist or ✕ cancels it
    /// and only the newest request touches the strip. Null when it was superseded.
    /// </summary>
    public async Task<Process.PlaylistFetchResult?> ReadPlaylistAsync(string url)
    {
        var vm = Vm; if (vm is null) return null;
        _inFlightCts?.Cancel();
        var cts = new CancellationTokenSource();
        _inFlightCts = cts;
        HideError();
        ShowParsing(true, "正在讀取播放清單…");
        try
        {
            var warmup = vm.Host.EnsureYtDlpReadyAsync();
            if (!warmup.IsCompleted)
            {
                ShowHint("元件首次啟動中，請稍候…（防毒軟體正在檢查，僅第一次需要）");
                await warmup.WaitAsync(cts.Token);
            }
            var result = await vm.Host.YtDlp.FetchPlaylistAsync(url, cts.Token);
            return cts.IsCancellationRequested || !ReferenceEquals(_inFlightCts, cts) ? null : result;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        finally
        {
            if (ReferenceEquals(_inFlightCts, cts)) ShowParsing(false);
        }
    }

    private void OnTextChanged(object sender, TextChangedEventArgs e)
    {
        var empty = string.IsNullOrEmpty(UrlTextBox.Text);
        Placeholder.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        ClearButton.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        HideError();
        _debounce.Stop();
        _debounce.Start();
    }

    private async void OnDebounceElapsed(object? sender, EventArgs e)
    {
        _debounce.Stop();
        var raw = UrlTextBox.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(raw))
        {
            // A parse still in flight for the previous text must not resurrect its video.
            _inFlightCts?.Cancel();
            ClearMetadata();
            return;
        }

        var result = new UrlValidator().Validate(raw);
        if (!result.IsValid)
        {
            _inFlightCts?.Cancel();
            ShowError(Strings.Get("Url.InvalidNotYouTube"));
            ClearMetadata();
            return;
        }

        if (result.IsPlaylist)
        {
            // v1.5.0: a playlist page — its videos are picked in a list instead.
            _inFlightCts?.Cancel();
            ClearMetadata();
            if (Main is { } main) await main.OpenPlaylistAsync(result.CanonicalUrl!);
            return;
        }

        await FetchMetadataAsync(result.CanonicalUrl!, result.PlaylistId);
    }

    private async Task FetchMetadataAsync(string url, string? playlistId)
    {
        var vm = Vm; if (vm is null) return;
        _inFlightCts?.Cancel();
        // v1.3.8: capture the CTS in a local. Every use below MUST go through `cts`,
        // never through the _inFlightCts field — a newer parse reassigns that field
        // while this call is suspended, so reading it later resolves to the SUCCESSOR's
        // token and this call sails on uncancelled. Log 2026-08-28: two
        // url.parse.started for one url_hash 2.8s apart, then two concurrent metadata
        // processes that starved each other.
        var cts = new CancellationTokenSource();
        _inFlightCts = cts;
        // An Enter-retry of the same text fires no TextChanged, so the previous attempt's
        // error would otherwise stay up beside this parse's result.
        HideError();
        ShowParsing(true);
        vm.IsParsing = true;

        var urlHash = AppLogger.HashSuffix(url);
        var sw = Stopwatch.StartNew();
        vm.Host.Logger.Info("url.parse.started", new Dictionary<string, string>
        {
            ["url_hash"] = urlHash
        });

        try
        {
            // v1.3.7: wait for the one-per-launch yt-dlp cold start instead of racing it.
            // Awaiting costs nothing once warm (the task is already completed) and turns
            // a slow first start into a wait with an explanation instead of a failure.
            var warmup = vm.Host.EnsureYtDlpReadyAsync();
            if (!warmup.IsCompleted)
            {
                ShowHint("元件首次啟動中，請稍候…（防毒軟體正在檢查，僅第一次需要）");
                // WaitAsync so a newer parse can abandon this wait instead of queueing
                // behind it and then launching a second yt-dlp alongside the first.
                await warmup.WaitAsync(cts.Token);
            }
            cts.Token.ThrowIfCancellationRequested();

            var result = await vm.Host.YtDlp.FetchMetadataAsync(url, cts.Token);
            // v1.5.0: FetchMetadataAsync reports cancellation as a failed result rather than
            // throwing, so a parse cancelled by ✕, by an invalid edit or by a newer link used
            // to show 「下載失敗：[cancelled]」 — or, when its process finished inside the kill
            // grace period, bring the old video back. Whatever it returned is stale: it must
            // not touch the UI, the log or the SSL prompt.
            if (cts.IsCancellationRequested || !ReferenceEquals(_inFlightCts, cts)) return;
            if (!result.IsSuccess || result.Metadata is null)
            {
                var mapped = ErrorMapper.Map(result.ErrorStderr ?? "");
                ShowError(mapped.UserMessage);
                ClearMetadata();
                vm.Host.Logger.Warn("url.parse.failed", new Dictionary<string, string>
                {
                    ["url_hash"] = urlHash,
                    ["category"] = mapped.Category.ToString(),
                    ["error_code"] = mapped.ErrorCode,
                    ["elapsed_ms"] = ((int)sw.ElapsedMilliseconds).ToString(),
                    ["details"] = TruncateForLog(result.ErrorStderr ?? "")
                });
                // v1.3.0/1.3.3: only offer the SSL bypass when the failure looks
                // SSL-related, so zero-output timeouts (yt-dlp launch failure) are not
                // mis-routed into "is this SSL?".
                if (MainViewModel.ShouldOfferSslPrompt(mapped))
                {
                    App.OfferSslBypassPrompt(vm.Host,
                        "貼上 YouTube 網址後解析失敗：" + mapped.UserMessage);
                }
                // v1.5.1: yt-dlp itself cannot start; offer the fix where the user is looking.
                if (ErrorMapper.CallsForRepair(mapped)) OfferRepair();
                return;
            }

            vm.CurrentMetadata = result.Metadata;
            vm.CurrentPlaylistId = playlistId;
            _ = LoadThumbnailAsync(vm, result.Metadata);
            vm.Host.Logger.Info("url.parse.completed", new Dictionary<string, string>
            {
                ["url_hash"] = urlHash,
                ["video_id"] = result.Metadata.VideoId,
                ["formats_count"] = result.Metadata.Formats.Count.ToString(),
                ["elapsed_ms"] = ((int)sw.ElapsedMilliseconds).ToString()
            });
        }
        catch (OperationCanceledException) { /* user typed more */ }
        finally
        {
            // Only the newest parse owns the indicator; a superseded one must not hide it.
            if (ReferenceEquals(_inFlightCts, cts))
            {
                ShowParsing(false);
                vm.IsParsing = false;
            }
        }
    }

    /// <summary>
    /// Collapses newlines to " | " and clips to 500 chars so a noisy yt-dlp stderr fits
    /// on one log line. Raw stderr stays in MappedError.RawDetails.
    /// </summary>
    private static string TruncateForLog(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var collapsed = s.Replace("\r\n", " | ").Replace('\n', '|').Replace('\r', '|').Trim();
        if (collapsed.Length > 500) collapsed = collapsed.Substring(0, 500);
        return collapsed;
    }

    private static async Task LoadThumbnailAsync(MainViewModel vm, VideoMetadata meta)
    {
        // Routed through InMemoryThumbnailLoader so nothing lands in WinINet's cache. The card
        // shows it 160 px wide; 320 keeps it sharp at 200 % scaling.
        var bmp = await InMemoryThumbnailLoader.LoadAsync(meta.ThumbnailUrl, maxPixelWidth: 320).ConfigureAwait(true);
        // A newer parse may have replaced the video while this was loading.
        if (bmp is not null && ReferenceEquals(vm.CurrentMetadata, meta)) vm.CurrentThumbnail = bmp;
    }

    private void ShowParsing(bool on, string? caption = null)
    {
        // Every start sets its own caption, so a playlist read's text never lingers on a
        // parse that follows it.
        if (on) ParsingCaption.Text = caption ?? Strings.Get("Url.Parsing");
        ParsingPanel.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        // The pulse animation runs exactly while IsIndeterminate is true. Collapsing the
        // bar alone would leave its clock ticking — an idle-CPU cost for nothing.
        ParsingBar.IsIndeterminate = on;
        if (!on) WarmupHint.Visibility = Visibility.Collapsed;
    }

    private void ShowHint(string message)
    {
        WarmupHint.Text = message;
        WarmupHint.Visibility = Visibility.Visible;
    }

    private void ShowError(string message)
    {
        ErrorLabel.Text = message;
        ErrorPanel.Visibility = Visibility.Visible;
    }

    private void HideError() => ErrorPanel.Visibility = Visibility.Collapsed;

    private void ClearMetadata()
    {
        if (Vm is null) return;
        Vm.CurrentMetadata = null;
        Vm.CurrentThumbnail = null;
        Vm.CurrentPlaylistId = null;
    }

    private void OnPasteClicked(object sender, RoutedEventArgs e)
    {
        string? text = null;
        try { if (Clipboard.ContainsText()) text = Clipboard.GetText(); }
        catch (System.Runtime.InteropServices.ExternalException) { /* clipboard held by another app */ }
        if (text is null) { UrlTextBox.Focus(); return; }
        if (Main is { } main) main.HandleIncomingText(text, fromDrop: false);
        else SetTextProgrammatically(text);
    }

    private void OnClearClicked(object sender, RoutedEventArgs e)
    {
        _inFlightCts?.Cancel();
        UrlTextBox.Text = "";
        ClearMetadata();
        UrlTextBox.Focus();
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

    public void SetTextProgrammatically(string text)
    {
        var trimmed = text.Trim();
        var same = UrlTextBox.Text == trimmed;
        UrlTextBox.Text = trimmed;
        UrlTextBox.CaretIndex = UrlTextBox.Text.Length;
        UrlTextBox.Focus();
        // v1.5.0: the same text raises no TextChanged. Pasting a link again after a failed
        // parse is how people retry, so parse it again explicitly.
        if (same)
        {
            _debounce.Stop();
            OnDebounceElapsed(this, EventArgs.Empty);
        }
    }

    /// <summary>Focus the URL box; optionally select its text so the next paste replaces it.</summary>
    public void FocusUrlBox(bool selectAll = false)
    {
        UrlTextBox.Focus();
        if (selectAll) UrlTextBox.SelectAll();
    }
}

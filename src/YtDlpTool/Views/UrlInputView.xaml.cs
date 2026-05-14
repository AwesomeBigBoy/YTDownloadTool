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

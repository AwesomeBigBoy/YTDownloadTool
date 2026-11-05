using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using YtDlpTool.Domain.Models;
using YtDlpTool.Domain.Services;
using YtDlpTool.Interop;
using YtDlpTool.ViewModels;

namespace YtDlpTool.Dialogs;

/// <summary>
/// v1.5.0: 加入多部影片 — the videos of a playlist or the links of a multi-link paste,
/// ticked by default (unavailable ones unticked and disabled), with one 格式 and one
/// 畫質 / 音質 for all of them. The quality is a ceiling: each video gets the best it has
/// at or below it (QualityOptions.PreferredIndex).
/// </summary>
public partial class BatchDialog : Window
{
    private static readonly (string Label, string Value)[] VideoQualities =
    {
        ("最高畫質", QualityOptions.Best),
        ("2160p (4K)", "2160p"),
        ("1440p (2K)", "1440p"),
        ("1080p", "1080p"),
        ("720p", "720p"),
        ("480p", "480p"),
        ("360p", "360p"),
        ("240p", "240p"),
        ("144p", "144p"),
    };

    private static readonly (string Label, string Value)[] AudioQualities =
    {
        ("M4A（不轉檔）", "M4A"),
        ("MP3（轉檔）", "MP3"),
    };

    private readonly IReadOnlyList<BatchItem> _items;
    private readonly string? _videoQuality;
    private readonly string? _audioQuality;
    private DownloadMode _mode;
    private BatchSelection? _result;

    private BatchDialog(string header, IReadOnlyList<BatchItem> items, DownloadMode mode,
        string? videoQuality, string? audioQuality, string note)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => DarkTitleBar.Apply(this);

        HeaderText.Text = header;
        NoteText.Text = note;
        _items = items;
        _videoQuality = videoQuality;
        _audioQuality = audioQuality;
        ItemsList.ItemsSource = items;
        foreach (var item in items) item.PropertyChanged += OnItemChanged;

        _mode = mode;
        (mode switch
        {
            DownloadMode.AudioOnly => AudioBtn,
            DownloadMode.VideoOnly => VideoBtn,
            _ => BothBtn,
        }).IsChecked = true;
        FillQualities();
        UpdateCount();
        Loaded += (_, _) => FocusFirstRow();
    }

    // The first video that can be added, not 加入: the row checkboxes take no focus, so with
    // focus on 加入 a Space meant to tick a row would start the batch. Enter still adds.
    private void FocusFirstRow()
    {
        var index = _items.ToList().FindIndex(i => i.IsAvailable);
        if (index >= 0)
        {
            ItemsList.SelectedIndex = index;
            ItemsList.ScrollIntoView(_items[index]);
            ItemsList.UpdateLayout();
            if (ItemsList.ItemContainerGenerator.ContainerFromIndex(index) is ListBoxItem row && row.Focus()) return;
        }
        AddButton.Focus();
    }

    /// <returns>The confirmed selection, or null when the user cancelled.</returns>
    public static BatchSelection? Ask(Window? owner, string header, IReadOnlyList<BatchItem> items,
        DownloadMode mode, string? videoQuality, string? audioQuality, string note)
    {
        var dlg = new BatchDialog(header, items, mode, videoQuality, audioQuality, note);
        if (owner is { IsVisible: true }) dlg.Owner = owner;
        else dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        return dlg.ShowDialog() == true ? dlg._result : null;
    }

    private void OnModeChecked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: string tag }) return;
        _mode = Enum.Parse<DownloadMode>(tag);
        if (IsInitialized) FillQualities();
    }

    private void FillQualities()
    {
        var audio = _mode == DownloadMode.AudioOnly;
        QualityLabel.Text = audio ? "音質" : "畫質";
        // A ceiling only means something for resolutions.
        QualityHint.Visibility = audio ? Visibility.Collapsed : Visibility.Visible;
        AutomationProperties.SetName(QualityCombo, QualityLabel.Text);
        var list = audio ? AudioQualities : VideoQualities;
        QualityCombo.ItemsSource = list.Select(q => q.Label).ToList();
        // The remembered choice when it is one of these; else 1080p / M4A.
        var remembered = audio ? _audioQuality : _videoQuality;
        var index = Array.FindIndex(list, q => string.Equals(q.Value, remembered, StringComparison.OrdinalIgnoreCase));
        QualityCombo.SelectedIndex = index >= 0 ? index : Array.FindIndex(list, q => q.Value is "1080p" or "M4A");
    }

    private void OnQualityChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_mode == DownloadMode.AudioOnly || QualityCombo.SelectedIndex < 0) return;
        var best = VideoQualities[QualityCombo.SelectedIndex].Value == QualityOptions.Best;
        QualityHint.Text = best ? "每部影片各用它的最高畫質" : "每部影片最高下載到此畫質";
        QualityHint.ToolTip = best ? null : CeilingRule;
    }

    private const string CeilingRule = "影片沒有這個畫質時，改用比它低的最高畫質；影片只有更高的畫質時，用其中最低的。";

    private void OnItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(BatchItem.IsSelected)) UpdateCount();
    }

    private void UpdateCount()
    {
        var selected = _items.Count(i => i.IsSelected);
        var unavailable = _items.Count(i => !i.IsAvailable);
        CountText.Text = $"共 {_items.Count} 部 · 已勾選 {selected} 部" +
                         (unavailable > 0 ? $" · {unavailable} 部無法下載" : "");
        AddButton.Content = $"加入 {selected} 部";
        AddButton.IsEnabled = selected > 0;
    }

    private void OnSelectAll(object sender, RoutedEventArgs e)
    {
        foreach (var item in _items) item.IsSelected = item.IsAvailable;
    }

    private void OnSelectNone(object sender, RoutedEventArgs e)
    {
        foreach (var item in _items) item.IsSelected = false;
    }

    // ↑↓ move through the rows (the checkboxes themselves take no focus); Space ticks.
    private void OnListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Space || ItemsList.SelectedItem is not BatchItem { IsAvailable: true } item) return;
        item.IsSelected = !item.IsSelected;
        e.Handled = true;
    }

    private void OnAdd(object sender, RoutedEventArgs e)
    {
        var urls = _items.Where(i => i.IsSelected).Select(i => i.Url).ToList();
        if (urls.Count == 0) return;
        var list = _mode == DownloadMode.AudioOnly ? AudioQualities : VideoQualities;
        var quality = list[Math.Max(0, QualityCombo.SelectedIndex)].Value;
        _result = new BatchSelection(urls, _mode, quality);
        DialogResult = true;
    }

    protected override void OnClosed(EventArgs e)
    {
        foreach (var item in _items) item.PropertyChanged -= OnItemChanged;
        base.OnClosed(e);
    }
}

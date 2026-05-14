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

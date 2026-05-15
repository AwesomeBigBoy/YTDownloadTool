using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using YtDlpTool.Domain.Models;

namespace YtDlpTool.ViewModels;

public partial class QueueItemViewModel : ObservableObject
{
    [ObservableProperty] private Guid _id;
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _thumbnailUrl = "";

    // Loaded by MainViewModel via InMemoryThumbnailLoader (Fix 4) — bound to the queue
    // panel's Image.Source so WPF never has to fetch the URL itself via WinINet.
    [ObservableProperty] private ImageSource? _thumbnailImage;

    [ObservableProperty] private JobStatus _status;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayMeta))]
    private double _progressPercent;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayMeta))]
    private long? _bytesPerSecond;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayMeta))]
    private TimeSpan? _eta;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayMeta))]
    private string? _failureReason;

    [ObservableProperty] private string? _outputFilePath;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayMeta))]
    private string _modeLabel = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayMeta))]
    private string _qualityLabel = "";

    public string DisplayMeta
    {
        get
        {
            var pct = $"{ProgressPercent:0.#}%";
            var speed = BytesPerSecond is { } b ? $" · {FormatSpeed(b)}" : "";
            var eta = Eta is { } t ? $" · 剩餘 {t:hh\\:mm\\:ss}" : "";
            var mode = $" · {QualityLabel} {ModeLabel}";
            var failure = FailureReason is { } r ? $"  ⚠ {r}" : "";
            return pct + speed + eta + mode + failure;
        }
    }

    private static string FormatSpeed(long b)
    {
        double v = b; var u = "B/s";
        if (v >= 1024) { v /= 1024; u = "KB/s"; }
        if (v >= 1024) { v /= 1024; u = "MB/s"; }
        return $"{v:0.#} {u}";
    }

    public void SetStatus(JobStatus s) => Status = s;
}

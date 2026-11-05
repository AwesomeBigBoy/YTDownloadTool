using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using YtDlpTool.Domain.Models;
using YtDlpTool.Domain.Services;

namespace YtDlpTool.ViewModels;

public partial class QueueItemViewModel : ObservableObject
{
    [ObservableProperty] private Guid _id;
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _thumbnailUrl = "";

    // Loaded by MainViewModel via InMemoryThumbnailLoader (Fix 4) — bound to the queue
    // panel's Image.Source so WPF never has to fetch the URL itself via WinINet.
    [ObservableProperty] private ImageSource? _thumbnailImage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(IsActive), nameof(IsDownloading),
                              nameof(IsCompleted), nameof(IsFailed))]
    private JobStatus _status;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private double _progressPercent;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private long? _bytesPerSecond;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private TimeSpan? _eta;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private string? _failureReason;

    [ObservableProperty] private string? _outputFilePath;

    /// <summary>v1.5.0: the request behind this row, so 「重試」 can queue it again.</summary>
    public DownloadJob? Job { get; set; }

    /// <summary>v1.5.0: 「重試」 puts the new attempt into this same row (place and thumbnail kept).</summary>
    public void Restart(DownloadJob job)
    {
        Job = job;
        Id = job.Id;
        _cancelling = false;
        FailureReason = null;
        OutputFilePath = null;
        UpdateProgress(0, null, null);
        Status = JobStatus.Pending;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TagText))]
    private string _modeLabel = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TagText))]
    private string _qualityLabel = "";

    // v1.5.0: yt-dlp's post-processors are running (QueueStatusText: 「80% · 處理中…」).
    private bool _processing;

    // v1.5.0: ✕ was pressed on a running download; the row goes when the queue confirms.
    private bool _cancelling;

    /// <summary>Shows 「正在取消…」 until the queue's cancelled event removes the row.</summary>
    public void MarkCancelling()
    {
        _cancelling = true;
        OnPropertyChanged(nameof(StatusText));
    }

    /// <summary>The line under the title; wording lives in Domain's QueueStatusText.</summary>
    public string StatusText => QueueStatusText.Format(Status, ProgressPercent, BytesPerSecond, Eta, FailureReason,
        _processing, _cancelling);

    /// <summary>What was asked for, e.g. 「1080p · 影片」.</summary>
    public string TagText => string.IsNullOrEmpty(QualityLabel) ? ModeLabel : $"{QualityLabel} · {ModeLabel}";

    public bool IsActive => Status is JobStatus.Pending or JobStatus.Downloading;
    public bool IsDownloading => Status == JobStatus.Downloading;
    public bool IsCompleted => Status == JobStatus.Completed;
    public bool IsFailed => Status == JobStatus.Failed;

    /// <summary>
    /// One progress tick: writes the three fields and raises StatusText once, instead of
    /// once per field through the generated setters (v1.5.0).
    /// </summary>
    // Writing the generated properties' backing fields is deliberate here: going through
    // the setters would raise StatusText three times per tick.
#pragma warning disable MVVMTK0034
    public void UpdateProgress(double percent, long? bytesPerSecond, TimeSpan? eta, bool processing = false)
    {
        var changed = false;
        if (_processing != processing) { _processing = processing; changed = true; }
        if (_progressPercent != percent) { _progressPercent = percent; OnPropertyChanged(nameof(ProgressPercent)); changed = true; }
        if (_bytesPerSecond != bytesPerSecond) { _bytesPerSecond = bytesPerSecond; OnPropertyChanged(nameof(BytesPerSecond)); changed = true; }
        if (_eta != eta) { _eta = eta; OnPropertyChanged(nameof(Eta)); changed = true; }
        if (changed) OnPropertyChanged(nameof(StatusText));
    }
#pragma warning restore MVVMTK0034

    public void SetStatus(JobStatus s) => Status = s;
}

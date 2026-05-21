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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAddDownload))]
    private string _saveDirectory = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAddDownload))]
    private VideoMetadata? _currentMetadata;

    [ObservableProperty] private DownloadMode _selectedMode = DownloadMode.AudioAndVideo;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAddDownload))]
    private VideoFormat? _selectedFormat;

    [ObservableProperty] private TimeRange? _clipRange;
    [ObservableProperty] private bool _isParsing;
    [ObservableProperty] private string? _parseError;
    [ObservableProperty] private bool _showFirstHint;
    [ObservableProperty] private QueueItemViewModel? _selectedQueueItem;

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
        // Intentionally do NOT clear URL/Format/Clip/Subs here — the user has just spent
        // effort dialling these in and frequently wants to enqueue the same video again
        // with a tweaked option (different quality, different clip). The reset happens
        // automatically in OnCurrentMetadataChanged when the URL resolves to a different
        // VideoId. See Fix 5 + Fix 6.
    }

    /// <summary>
    /// Reset video-specific options when the URL resolves to a different video. We compare
    /// VideoIds rather than raw URLs so a youtu.be link and the youtube.com equivalent
    /// don't trigger a spurious reset. Transitions null→meta (first paste) and meta→null
    /// (clear/error) leave the user's last-selected options alone.
    /// </summary>
    partial void OnCurrentMetadataChanged(VideoMetadata? oldValue, VideoMetadata? newValue)
    {
        if (!MetadataChangePolicy.ShouldResetOptions(oldValue, newValue)) return;
        SelectedFormat = null;
        ClipRange = null;
        SelectedSubtitleLanguages.Clear();
    }

    [RelayCommand]
    private void CancelJob(Guid id)
    {
        var item = Queue.FirstOrDefault(q => q.Id == id);
        if (item is null) return;
        if (item.Status is JobStatus.Completed or JobStatus.Failed or JobStatus.Cancelled)
        {
            // Already terminal — the row is now a 'remove from list' affordance.
            Queue.Remove(item);
        }
        else
        {
            // Active job — kick off cancellation; OnQueueEvent will remove the row
            // once JobCancelledEvent propagates back from the queue.
            _host.Queue.Cancel(id);
        }
    }

    [RelayCommand]
    private void RemoveCompleted()
    {
        var terminal = Queue.Where(q =>
            q.Status is JobStatus.Completed or JobStatus.Failed or JobStatus.Cancelled).ToList();
        foreach (var item in terminal) Queue.Remove(item);
    }

    private void OnQueueEvent(object? sender, QueueEvent evt)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null) return;
        dispatcher.Invoke(() =>
        {
            switch (evt)
            {
                case JobEnqueuedEvent e:
                    var newItem = new QueueItemViewModel
                    {
                        Id = e.Job.Id,
                        Title = e.Job.Title,
                        ThumbnailUrl = e.Job.ThumbnailUrl,
                        Status = JobStatus.Pending,
                        ModeLabel = ModeLabel(e.Job.Mode),
                        QualityLabel = QualityLabel(e.Job.ChosenFormat)
                    };
                    Queue.Add(newItem);
                    // Load the thumbnail in-memory so the queue row's <Image> never has
                    // to round-trip through WinINet (which would write to INetCache).
                    _ = LoadQueueThumbnailAsync(newItem, e.Job.ThumbnailUrl);
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
                    Interop.ToastService.NotifyDownloadCompleted(e.Job.Title, e.OutputFilePath);
                    break;
                case JobFailedEvent e:
                    var f = Find(e.Job.Id);
                    if (f is not null)
                    {
                        f.SetStatus(JobStatus.Failed);
                        f.FailureReason = e.Error.UserMessage;
                    }
                    Interop.ToastService.NotifyDownloadFailed(e.Job.Title, e.Error.UserMessage);
                    // v1.3.0: if this was an SSL verification failure and the user
                    // hasn't already opted into the bypass, offer it now with full
                    // security context. App.OfferSslBypassPrompt handles
                    // dedup-per-session, the modal warning, config save, and
                    // auto-restart in one call.
                    if (IsSslFailureCode(e.Error.ErrorCode))
                    {
                        App.OfferSslBypassPrompt(_host,
                            $"下載「{e.Job.Title}」失敗：{e.Error.UserMessage}");
                    }
                    break;
                case JobCancelledEvent e:
                    var x = Find(e.Job.Id);
                    if (x is not null)
                    {
                        x.SetStatus(JobStatus.Cancelled);
                        // User clicked ✕ on an active row — surface this as 'gone from the
                        // list' so the affordance behaves the same as removing a terminal item.
                        Queue.Remove(x);
                    }
                    break;
            }
        });
    }

    private QueueItemViewModel? Find(Guid id) => Queue.FirstOrDefault(q => q.Id == id);

    // v1.3.0: SSL-related error codes that suggest the user is on a network that
    // intercepts HTTPS — used to gate the "enable bypass + restart?" prompt.
    private static bool IsSslFailureCode(string? code) =>
        !string.IsNullOrEmpty(code) && (code == "E-SSL01" || code == "E-SSL02");

    internal static bool IsSslErrorCode(string? code) => IsSslFailureCode(code);

    private static async Task LoadQueueThumbnailAsync(QueueItemViewModel item, string url)
    {
        var bmp = await Interop.InMemoryThumbnailLoader.LoadAsync(url).ConfigureAwait(true);
        if (bmp is not null) item.ThumbnailImage = bmp;
    }

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

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

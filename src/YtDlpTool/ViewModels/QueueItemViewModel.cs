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

    public void SetStatus(JobStatus s) => Status = s;
}

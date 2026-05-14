using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using YtDlpTool.Domain.Models;

namespace YtDlpTool.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly AppHost _host;

    public MainViewModel(AppHost host)
    {
        _host = host;
        SaveDirectory = host.Config.DefaultSaveDirectory;
        Queue = new ObservableCollection<QueueItemViewModel>();
    }

    [ObservableProperty] private string _saveDirectory = "";
    [ObservableProperty] private VideoMetadata? _currentMetadata;
    [ObservableProperty] private DownloadMode _selectedMode = DownloadMode.AudioAndVideo;
    [ObservableProperty] private VideoFormat? _selectedFormat;
    [ObservableProperty] private TimeRange? _clipRange;
    [ObservableProperty] private bool _isParsing;
    [ObservableProperty] private string? _parseError;
    [ObservableProperty] private bool _showFirstHint;

    public ObservableCollection<string> SelectedSubtitleLanguages { get; } = new();
    public ObservableCollection<QueueItemViewModel> Queue { get; }

    public AppHost Host => _host;
}

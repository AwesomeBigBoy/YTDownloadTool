using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using YtDlpTool.Domain.Updates;

namespace YtDlpTool.ViewModels;

public partial class UpdateBannerViewModel : ObservableObject
{
    [ObservableProperty] private bool _isVisible;
    [ObservableProperty] private string _headline = "";
    [ObservableProperty] private bool _isApplying;
    [ObservableProperty] private string _applyStatus = "";
    [ObservableProperty] private double _applyPercent;
    [ObservableProperty] private bool _hasFailure;
    [ObservableProperty] private string? _failureReason;
    public ObservableCollection<ManifestFileEntry> Entries { get; } = new();
}

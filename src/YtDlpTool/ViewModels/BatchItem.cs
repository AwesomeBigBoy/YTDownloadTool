using CommunityToolkit.Mvvm.ComponentModel;
using YtDlpTool.Domain.Models;

namespace YtDlpTool.ViewModels;

/// <summary>v1.5.0: one row of the 加入多部影片 list (a playlist's video or a pasted link).</summary>
public sealed partial class BatchItem : ObservableObject
{
    public BatchItem(string url, string title, string detail, bool isAvailable)
    {
        Url = url;
        Title = title;
        Detail = detail;
        IsAvailable = isAvailable;
        _isSelected = isAvailable;
    }

    public string Url { get; }
    public string Title { get; }

    /// <summary>The length (「3:25」), or why the video cannot be added.</summary>
    public string Detail { get; }

    public bool IsAvailable { get; }

    [ObservableProperty] private bool _isSelected;
}

/// <summary>v1.5.0: what the user confirmed in the 加入多部影片 dialog.</summary>
/// <param name="Quality">As QualityOptions.PreferredIndex reads it: "1080p", "best", "M4A", "MP3".</param>
public sealed record BatchSelection(IReadOnlyList<string> Urls, DownloadMode Mode, string Quality);

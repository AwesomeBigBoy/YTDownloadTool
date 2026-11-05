using CommunityToolkit.Mvvm.ComponentModel;

namespace YtDlpTool.ViewModels;

/// <summary>
/// Startup component-health warning.
///
/// v1.3.6: AppHost already probes <c>yt-dlp.exe --version</c> at startup and logs
/// <c>version.probe ytdlp=(probe-failed)</c> when it does not answer — but nothing
/// surfaced that to the user. In the 2026-08 field report the app knew yt-dlp was
/// broken at 15:02:08 and stayed silent while the user waited out a 38-second parse
/// failure at 15:03:14. This banner closes that gap: if the component that does all
/// the work cannot even report its own version, say so before the user tries to use it.
/// </summary>
public partial class HealthBannerViewModel : ObservableObject
{
    [ObservableProperty] private bool _isVisible;
    [ObservableProperty] private string _headline = "";

    // v1.5.0: one-click 修復 (AppHost.RepairYtDlpAsync).
    [ObservableProperty] private bool _canRepair;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowRepairStatus))]
    private bool _isRepairing;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowRepairStatus))]
    private string _repairStatus = "";

    [ObservableProperty] private double _repairPercent;

    /// <summary>Status line under the headline: while repairing, or to show its outcome.</summary>
    public bool ShowRepairStatus => IsRepairing || !string.IsNullOrEmpty(RepairStatus);

    /// <summary>Long-form remediation text shown by the "詳細說明" button.</summary>
    public string Details { get; set; } = "";
}

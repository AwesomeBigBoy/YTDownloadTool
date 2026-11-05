using System.Windows;
using YtDlpTool.Domain.Updates;

namespace YtDlpTool.Dialogs;

/// <summary>
/// v1.5.0: what the update banner and Settings run after UpdateApplier succeeds, when the
/// files it installed include yt-dlp. A new package used to sit unexpanded until the next
/// launch while the UI already said 「已更新」; this puts it into service now
/// (AppHost.ActivateYtDlpPackageAsync) and tells the user when that is not possible.
/// </summary>
internal static class YtDlpUpdateFinisher
{
    public static bool Includes(IEnumerable<ManifestFileEntry> applied) =>
        applied.Any(e => e.Component == UpdateComponent.YtDlp);

    /// <returns>The activation result; when it is not fixed, its Message is the status line to show.</returns>
    public static async Task<YtDlpRepairResult> FinishAsync(AppHost host, Window? owner, string trigger)
    {
        var result = await host.ActivateYtDlpPackageAsync(trigger);
        if (result.NeedsRestart)
        {
            var answer = AppDialog.Show(owner, result.Message + "。要現在重新啟動嗎？", "需要重新啟動",
                MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer == MessageBoxResult.Yes) App.RestartApplication();
        }
        else if (!result.IsFixed)
        {
            AppDialog.Show(owner, result.Message, "yt-dlp 元件", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        return result;
    }
}

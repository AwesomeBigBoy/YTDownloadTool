using System.Windows;
using YtDlpTool.Domain.Updates;

namespace YtDlpTool.Dialogs;

/// <summary>
/// v1.5.1: 修復 (AppHost.RepairYtDlpAsync) with its progress on the health banner and its
/// outcome in a dialog. Shared by the banner's button and by the offer a parse makes when it
/// failed because yt-dlp cannot start (UrlInputView.OfferRepair), so both behave alike.
/// </summary>
internal static class YtDlpRepairFlow
{
    private static bool _running;

    /// <returns>True when yt-dlp answers again.</returns>
    public static async Task<bool> RunAsync(AppHost host, Window? owner)
    {
        // The banner hides its button while a repair runs; this covers the other caller.
        if (_running) return false;
        _running = true;

        var vm = host.HealthVm;
        vm.IsVisible = true;
        vm.IsRepairing = true;
        vm.RepairPercent = 0;
        vm.RepairStatus = "正在取得元件清單…";
        var progress = new Progress<UpdateApplyProgress>(p =>
        {
            vm.RepairPercent = p.FilePercent;
            vm.RepairStatus = p.Stage switch
            {
                UpdateApplyStage.Downloading        => $"正在下載 yt-dlp 元件… {p.FilePercent:0}%",
                UpdateApplyStage.VerifyingHash      => "正在驗證檔案…",
                UpdateApplyStage.VerifyingSignature => "正在驗證簽章…",
                UpdateApplyStage.Applying           => "正在換上新元件…",
                _                                   => vm.RepairStatus,
            };
        });

        YtDlpRepairResult result;
        try
        {
            result = await host.RepairYtDlpAsync(progress, CancellationToken.None);
        }
        catch (Exception ex)
        {
            result = new YtDlpRepairResult(false, false, $"修復失敗：{ex.Message}");
        }
        finally
        {
            vm.IsRepairing = false;
            _running = false;
        }

        if (result.IsFixed)
        {
            vm.RepairStatus = "";
            vm.CanRepair = false;
            vm.IsVisible = false;
            AppDialog.Show(owner, $"yt-dlp 元件已修復（版本 {result.Message}），可以開始下載了。",
                "修復完成", MessageBoxButton.OK, MessageBoxImage.Information);
            return true;
        }
        if (result.NeedsRestart)
        {
            var answer = AppDialog.Show(owner, result.Message + "。要現在重新啟動嗎？",
                "需要重新啟動", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer == MessageBoxResult.Yes) App.RestartApplication();
            else vm.RepairStatus = result.Message;
            return false;
        }
        vm.RepairStatus = result.Message;
        return false;
    }
}

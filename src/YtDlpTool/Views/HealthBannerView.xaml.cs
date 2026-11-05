using System.Windows;
using System.Windows.Controls;
using YtDlpTool.Dialogs;
using YtDlpTool.Domain.Updates;
using YtDlpTool.ViewModels;

namespace YtDlpTool.Views;

public partial class HealthBannerView : UserControl
{
    private HealthBannerViewModel? Vm => DataContext as HealthBannerViewModel;

    public HealthBannerView()
    {
        InitializeComponent();
    }

    private void OnDetailsClicked(object sender, RoutedEventArgs e)
    {
        if (Vm is null) return;
        AppDialog.Show(Window.GetWindow(this), Vm.Details, "元件狀態", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void OnDismissClicked(object sender, RoutedEventArgs e)
    {
        if (Vm is not null) Vm.IsVisible = false;
    }

    // v1.5.0: see AppHost.RepairYtDlpAsync for why this button exists.
    private async void OnRepairClicked(object sender, RoutedEventArgs e)
    {
        var host = (Application.Current as App)?.Host;
        var vm = Vm;
        if (host is null || vm is null) return;

        RepairButton.IsEnabled = false;
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
            RepairButton.IsEnabled = true;
        }

        if (result.IsFixed)
        {
            vm.RepairStatus = "";
            vm.CanRepair = false;
            vm.IsVisible = false;
            AppDialog.Show(Window.GetWindow(this), $"yt-dlp 元件已修復（版本 {result.Message}），可以開始下載了。",
                "修復完成", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (result.NeedsRestart)
        {
            var answer = AppDialog.Show(Window.GetWindow(this), result.Message + "。要現在重新啟動嗎？",
                "需要重新啟動", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer == MessageBoxResult.Yes) App.RestartApplication();
            else vm.RepairStatus = result.Message;
            return;
        }
        vm.RepairStatus = result.Message;
    }
}

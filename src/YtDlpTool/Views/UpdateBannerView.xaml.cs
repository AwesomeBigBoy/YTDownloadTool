using System.Windows;
using System.Windows.Controls;
using YtDlpTool.Domain.Updates;
using YtDlpTool.ViewModels;

namespace YtDlpTool.Views;

public partial class UpdateBannerView : UserControl
{
    private AppHost? Host => (Application.Current as App)?.Host;
    private UpdateBannerViewModel? Vm => DataContext as UpdateBannerViewModel;

    public UpdateBannerView()
    {
        InitializeComponent();
    }

    private async void OnOneClickClicked(object sender, RoutedEventArgs e)
    {
        if (Host is null || Vm is null) return;
        Vm.IsApplying = true;
        Vm.ApplyStatus = "下載中…";
        Vm.ApplyPercent = 0;

        var progress = new Progress<UpdateApplyProgress>(p =>
        {
            Vm.ApplyStatus = p.Stage switch
            {
                UpdateApplyStage.Downloading       => $"下載中 · {p.FileName} · {p.FilePercent:0}%",
                UpdateApplyStage.VerifyingHash     => $"驗證雜湊 · {p.FileName}",
                UpdateApplyStage.VerifyingSignature => $"驗證簽章 · {p.FileName}",
                UpdateApplyStage.Applying          => $"套用中 · {p.FileName}",
                UpdateApplyStage.Done              => "✓ 已更新",
                UpdateApplyStage.RolledBack        => "已還原",
                _ => Vm.ApplyStatus
            };
            Vm.ApplyPercent = (double)(p.FileIndex - 1) / Math.Max(1, p.FileCount) * 100 + p.FilePercent / p.FileCount;
        });

        var result = await Host.UpdateApplier.ApplyAsync(
            Vm.Entries.ToList(), progress, CancellationToken.None);

        if (result.IsSuccess)
        {
            Vm.ApplyStatus = "✓ 已更新";
            await Task.Delay(1500);
            Vm.IsVisible = false;
            Vm.IsApplying = false;
        }
        else
        {
            Vm.IsApplying = false;
            Vm.HasFailure = true;
            Vm.FailureReason = result.FailureReason;
            MessageBox.Show(
                $"更新失敗，已自動還原。\n\n詳情：{result.FailureReason}",
                "YtDlpTool", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnLaterClicked(object sender, RoutedEventArgs e)
    {
        if (Vm is not null) Vm.IsVisible = false;
    }
}

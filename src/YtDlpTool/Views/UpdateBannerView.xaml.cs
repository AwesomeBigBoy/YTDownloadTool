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

        // v1.3.1: log which entries we are about to apply so the post-update log
        // tells exactly which components got replaced.
        var entries = Vm.Entries.ToList();
        Host.Logger.Info("update.apply.entries", new Dictionary<string, string>
        {
            ["count"] = entries.Count.ToString(),
            ["names"] = string.Join(",", entries.Select(e => e.Name)),
            ["trigger"] = "banner_one_click",
        });

        var result = await Host.UpdateApplier.ApplyAsync(entries, progress, CancellationToken.None);

        if (result.IsSuccess)
        {
            Vm.ApplyStatus = "✓ 已更新";
            // v1.3.1/v1.3.2: if YtDlpTool.exe itself was updated, force-restart.
            // No Yes/No — the user clicked "one-click update" so they've already
            // committed; making them choose to apply it (or stay on old binary)
            // is bad UX.
            var appExeUpdated = entries.Any(en =>
                string.Equals(en.Name, "YtDlpTool.exe", StringComparison.OrdinalIgnoreCase));
            if (appExeUpdated)
            {
                MessageBox.Show(
                    "YtDlpTool 主程式已更新完成，將立刻重新啟動套用新版本。",
                    "更新完成",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                App.RestartApplication();
                return;
            }
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

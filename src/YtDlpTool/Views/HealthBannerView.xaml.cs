using System.Windows;
using System.Windows.Controls;
using YtDlpTool.Dialogs;
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

    // v1.5.0: see AppHost.RepairYtDlpAsync for why this button exists. v1.5.1: the flow is
    // shared with the offer a failed parse makes (YtDlpRepairFlow).
    private async void OnRepairClicked(object sender, RoutedEventArgs e)
    {
        if ((Application.Current as App)?.Host is not { } host) return;
        await YtDlpRepairFlow.RunAsync(host, Window.GetWindow(this));
    }
}

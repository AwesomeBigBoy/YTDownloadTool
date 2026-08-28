using System.Windows;
using System.Windows.Controls;
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
        MessageBox.Show(Vm.Details, "元件狀態", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void OnDismissClicked(object sender, RoutedEventArgs e)
    {
        if (Vm is not null) Vm.IsVisible = false;
    }
}

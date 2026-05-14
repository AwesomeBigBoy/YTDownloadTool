using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using YtDlpTool.Domain.Models;
using YtDlpTool.ViewModels;

namespace YtDlpTool.Views;

public partial class FormatSelectorView : UserControl
{
    private MainViewModel? Vm => DataContext as MainViewModel;

    public FormatSelectorView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => RefreshSelection();
        Loaded += (_, _) => RefreshSelection();
    }

    private void OnModeClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not string tag || Vm is null) return;
        Vm.SelectedMode = Enum.Parse<DownloadMode>(tag);
        RefreshSelection();
    }

    private void RefreshSelection()
    {
        if (Vm is null) return;
        ApplyVisual(AudioBtn, Vm.SelectedMode == DownloadMode.AudioOnly);
        ApplyVisual(BothBtn,  Vm.SelectedMode == DownloadMode.AudioAndVideo);
        ApplyVisual(VideoBtn, Vm.SelectedMode == DownloadMode.VideoOnly);
    }

    private static void ApplyVisual(Button b, bool selected)
    {
        if (selected)
        {
            b.Background = (Brush)Application.Current.FindResource("Brush.Accent");
            b.Foreground = Brushes.White;
        }
        else
        {
            b.Background = Brushes.Transparent;
            b.Foreground = (Brush)Application.Current.FindResource("Brush.TextSecondary");
        }
    }
}

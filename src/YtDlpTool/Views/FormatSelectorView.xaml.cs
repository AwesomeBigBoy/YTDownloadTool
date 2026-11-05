using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using YtDlpTool.Domain.Models;
using YtDlpTool.ViewModels;

namespace YtDlpTool.Views;

public partial class FormatSelectorView : UserControl
{
    private MainViewModel? Vm => DataContext as MainViewModel;

    public FormatSelectorView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    // v1.5.0: the mode also changes from outside this control (a playlist or batch picks
    // one), so the buttons follow the view model instead of reading it only once.
    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is MainViewModel oldVm) oldVm.PropertyChanged -= OnVmChanged;
        if (e.NewValue is MainViewModel newVm) newVm.PropertyChanged += OnVmChanged;
        RefreshSelection();
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.SelectedMode)) RefreshSelection();
    }

    private void OnModeChecked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string tag } && Vm is not null)
            Vm.SelectedMode = Enum.Parse<DownloadMode>(tag);
    }

    private void RefreshSelection()
    {
        if (Vm is null) return;
        BothBtn.IsChecked = Vm.SelectedMode == DownloadMode.AudioAndVideo;
        AudioBtn.IsChecked = Vm.SelectedMode == DownloadMode.AudioOnly;
        VideoBtn.IsChecked = Vm.SelectedMode == DownloadMode.VideoOnly;
    }
}

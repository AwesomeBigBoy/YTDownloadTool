using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using YtDlpTool.Domain.Services;
using YtDlpTool.ViewModels;

namespace YtDlpTool.Views;

public partial class QualityDropdown : UserControl
{
    private MainViewModel? Vm => DataContext as MainViewModel;

    public QualityDropdown()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is MainViewModel oldVm) oldVm.PropertyChanged -= OnVmChanged;
        if (e.NewValue is MainViewModel newVm) newVm.PropertyChanged += OnVmChanged;
        Rebuild();
    }

    private void OnVmChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.CurrentMetadata) or nameof(MainViewModel.SelectedMode))
            Rebuild();
    }

    // v1.5.0: what the list offers is decided by QualityOptions (Domain, unit-tested):
    // every resolution once, H.264 preferred, video-only streams preferred, Shorts by
    // their shorter side, 1080p preselected.
    private void Rebuild()
    {
        if (Vm is null || Vm.CurrentMetadata is null)
        {
            QualityCombo.ItemsSource = null;
            return;
        }
        var choices = QualityOptions.Build(Vm.SelectedMode, Vm.CurrentMetadata.Formats);
        var audio = Vm.SelectedMode == Domain.Models.DownloadMode.AudioOnly;
        QualityLabel.Text = audio ? "音質" : "畫質";
        AutomationProperties.SetName(QualityCombo, QualityLabel.Text);
        QualityCombo.ItemsSource = choices;
        QualityCombo.IsEnabled = choices.Count > 0;
        QualityCombo.Visibility = choices.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        NoFormatsText.Visibility = choices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        // v1.5.0: the 畫質 / 音質 of the last download, or the nearest lower one this video has.
        QualityCombo.SelectedIndex = QualityOptions.PreferredIndex(choices, Vm.PreferredQuality(Vm.SelectedMode));
        if (choices.Count == 0) Vm.SelectedFormat = null;
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Vm is null) return;
        if (QualityCombo.SelectedItem is QualityChoice choice) Vm.SelectedFormat = choice.Format;
    }
}

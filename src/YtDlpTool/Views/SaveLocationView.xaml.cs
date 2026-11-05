using System.IO;
using System.Windows;
using System.Windows.Controls;
using YtDlpTool.ViewModels;

namespace YtDlpTool.Views;

public partial class SaveLocationView : UserControl
{
    private MainViewModel? Vm => DataContext as MainViewModel;

    public SaveLocationView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is MainViewModel oldVm) oldVm.PropertyChanged -= OnVmChanged;
        if (e.NewValue is MainViewModel newVm)
        {
            newVm.PropertyChanged += OnVmChanged;
            ShowPath(newVm.SaveDirectory);
        }
    }

    private void OnVmChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.SaveDirectory) && Vm is not null)
            ShowPath(Vm.SaveDirectory);
    }

    // v1.5.0: 「桌面\YtVideo」 reads better than C:\Users\<name>\Desktop\YtVideo; the
    // tooltip keeps the full path.
    private void ShowPath(string path)
    {
        PathLabel.Text = FriendlyPath(path);
        PathLabel.ToolTip = path;
    }

    internal static string FriendlyPath(string path)
    {
        if (string.IsNullOrEmpty(path)) return "";
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        if (!string.IsNullOrEmpty(desktop) &&
            path.StartsWith(desktop, StringComparison.OrdinalIgnoreCase) &&
            (path.Length == desktop.Length || path[desktop.Length] == Path.DirectorySeparatorChar))
        {
            return "桌面" + path[desktop.Length..];
        }
        return path;
    }

    private void OnBrowseClicked(object sender, RoutedEventArgs e)
    {
        if (Vm is null) return;
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "選擇下載資料夾",
            InitialDirectory = Directory.Exists(Vm.SaveDirectory) ? Vm.SaveDirectory : ""
        };
        if (dlg.ShowDialog() == true && !string.IsNullOrEmpty(dlg.FolderName))
        {
            Vm.SaveDirectory = dlg.FolderName;
            Vm.Host.Config.DefaultSaveDirectory = dlg.FolderName;
            Vm.Host.ConfigStore.Save(Vm.Host.Config);
        }
    }
}

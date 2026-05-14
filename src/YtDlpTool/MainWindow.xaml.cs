using System.Windows;
using YtDlpTool.Interop;
using YtDlpTool.ViewModels;

namespace YtDlpTool;

public partial class MainWindow : Window
{
    public MainViewModel? ViewModel { get; private set; }

    public MainWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => WindowChromeHelper.ApplyAuroraBackdrop(this);
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var host = ((App)Application.Current).Host
            ?? throw new InvalidOperationException("AppHost not initialised");
        ViewModel = new MainViewModel(host);
        DataContext = ViewModel;
        UpdateBanner.DataContext = host.BannerVm;
        Activated += (_, _) => UrlInput.RefreshPasteHint();
    }

    private void OnSettingsClicked(object sender, RoutedEventArgs e)
    {
        var host = ((App)Application.Current).Host;
        if (host is null) return;
        var dlg = new Dialogs.SettingsDialog(host) { Owner = this };
        dlg.ShowDialog();
    }

    private void OnAddDownloadClicked(object sender, RoutedEventArgs e)
    {
        ViewModel?.AddDownloadCommand.Execute(null);
    }
}

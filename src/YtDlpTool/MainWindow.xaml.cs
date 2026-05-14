using System.Windows;
using System.Windows.Input;
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

        var interrupted = host.ReadAndClearInterruptedJobs();
        if (interrupted.Count > 0)
        {
            var msg = $"上次有 {interrupted.Count} 個下載尚未完成。\n\n" +
                      string.Join("\n", interrupted.Take(5).Select(j => "· " + j.Title)) +
                      (interrupted.Count > 5 ? $"\n…還有 {interrupted.Count - 5} 個" : "") +
                      "\n\n要把它們重新加回佇列嗎？";
            var result = MessageBox.Show(msg, "恢復下載", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result == MessageBoxResult.Yes)
            {
                // We don't store enough to fully reconstruct VideoFormat without metadata,
                // so we surface the URLs and let the user re-paste. v1.x compromise per spec.
                Clipboard.SetText(string.Join("\n", interrupted.Select(j => j.Url)));
                MessageBox.Show("已將未完成下載的網址複製到剪貼簿，請依序貼上重新加入。",
                    "恢復下載", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
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

    public ICommand PasteFromClipboardCommand => new RelayCommandAdapter(_ =>
    {
        if (!Clipboard.ContainsText()) return;
        var text = Clipboard.GetText();
        UrlInput?.SetTextProgrammatically(text);
    });

    public ICommand CancelSelectedQueueCommand => new RelayCommandAdapter(_ =>
    {
        // No selectable queue item yet (Phase 8 keeps the list non-selectable).
        // Reserved for future use; currently a no-op to satisfy the keybinding.
    });

    private sealed class RelayCommandAdapter : ICommand
    {
        private readonly Action<object?> _execute;
        public RelayCommandAdapter(Action<object?> execute) => _execute = execute;
        public event EventHandler? CanExecuteChanged
        {
            add    { CommandManager.RequerySuggested += value; }
            remove { CommandManager.RequerySuggested -= value; }
        }
        public bool CanExecute(object? p) => true;
        public void Execute(object? p) => _execute(p);
    }
}

using System.IO;
using System.Reflection;
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

        // v1.2.3: surface the version in two places (taskbar title + header label) so
        // user bug reports always carry the build identifier. release.yml sets
        // AssemblyInformationalVersion at build time from the git tag (e.g. "1.2.3");
        // dev builds report "0.0.0" because the csproj has no <Version> element.
        var version = GetAppVersion();
        VersionLabel.Text = "v" + version;
        Title = $"YtDlpTool v{version}";

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

    private void OnOpenFolderClicked(object sender, RoutedEventArgs e)
    {
        // Surfaces the same affordance previously buried under Settings → 進階. We use
        // ProcessStartInfo with UseShellExecute=true so Windows treats this like a normal
        // explorer.exe invocation (which is what the user expects).
        var host = ((App)Application.Current).Host;
        if (host is null) return;
        var dir = host.Config.DefaultSaveDirectory;
        try
        {
            Directory.CreateDirectory(dir);
            // Fully qualify System.Diagnostics.Process — the project's own YtDlpTool.Process
            // namespace shadows the unqualified "Process" identifier here.
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", dir)
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"無法開啟資料夾：{ex.Message}", "錯誤", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
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
        if (ViewModel?.SelectedQueueItem is { } item)
        {
            ViewModel.Host.Queue.Cancel(item.Id);
        }
    });

    // v1.2.3: Pulls AssemblyInformationalVersion (set by release.yml from the git tag,
    // e.g. "1.2.3") rather than AssemblyVersion (which is always padded to 4 segments,
    // "1.2.3.0"). Falls back to "0.0.0" for dev builds where the csproj has no <Version>.
    internal static string GetAppVersion()
    {
        var info = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(info))
        {
            // Strip the SourceRevisionId suffix that the SDK appends (e.g. "1.2.3+abc123").
            var plus = info.IndexOf('+');
            return plus > 0 ? info[..plus] : info;
        }
        return Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";
    }

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

using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Shell;
using YtDlpTool.Dialogs;
using YtDlpTool.Domain.Logging;
using YtDlpTool.Domain.Models;
using YtDlpTool.Domain.Services;
using YtDlpTool.Interop;
using YtDlpTool.ViewModels;
using YtDlpTool.Views;

namespace YtDlpTool;

public partial class MainWindow : Window
{
    public MainViewModel? ViewModel { get; private set; }

    public MainWindow()
    {
        InitializeComponent();
        // v1.5.0: native title bar, darkened before the window is first shown.
        SourceInitialized += (_, _) => DarkTitleBar.Apply(this);
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var host = ((App)Application.Current).Host
            ?? throw new InvalidOperationException("AppHost not initialised");
        ViewModel = new MainViewModel(host);
        DataContext = ViewModel;
        UpdateBanner.DataContext = host.BannerVm;
        HealthBanner.DataContext = host.HealthVm;

        // v1.2.3: the version is in the title bar so every bug-report screenshot carries
        // the build. release.yml sets AssemblyInformationalVersion from the git tag; dev
        // builds report the SDK default.
        Title = $"YtDlpTool v{GetAppVersion()}";

        UrlInput.FocusUrlBox();

        // v1.5.0: download progress on the taskbar button.
        TaskbarItemInfo = new TaskbarItemInfo();
        ViewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(MainViewModel.TaskbarProgressState)
                                  or nameof(MainViewModel.TaskbarProgressValue))
                ApplyTaskbarProgress();
        };

        var interrupted = host.ReadAndClearInterruptedJobs();
        if (interrupted.Count > 0)
        {
            // v1.5.0: straight back into the list — each link is parsed again, like a
            // playlist — instead of copying the links to the clipboard to paste one by one.
            // Downloads that were running and links of a playlist or batch that were not
            // queued yet (BatchJournal, no title) both come back here.
            var items = interrupted
                .DistinctBy(j => j.Url)
                .Select(j => new BatchItem(j.Url, string.IsNullOrWhiteSpace(j.Title) ? j.Url : j.Title, "", true))
                .ToList();
            var modes = interrupted.Select(j => j.Mode).Distinct().ToList();
            OpenBatch(items, $"上次有 {items.Count} 部影片沒有下載完", "勾選要重新加入下載清單的影片。",
                modes.Count == 1 ? modes[0] : null);
        }
    }

    private void ApplyTaskbarProgress()
    {
        if (ViewModel is null || TaskbarItemInfo is null) return;
        TaskbarItemInfo.ProgressState = ViewModel.TaskbarProgressState switch
        {
            TaskbarState.Normal => TaskbarItemProgressState.Normal,
            TaskbarState.Indeterminate => TaskbarItemProgressState.Indeterminate,
            _ => TaskbarItemProgressState.None,
        };
        TaskbarItemInfo.ProgressValue = ViewModel.TaskbarProgressValue;
    }

    // ── v1.5.0: links in — paste, Ctrl+V, drag and drop ────────────────────────

    /// <summary>
    /// v1.5.0: the one way in for links: 貼上, Ctrl+V and drops all land here. One video
    /// goes into the URL box as before; a playlist opens the list of its videos; several
    /// links open that same list for them. Pasted text without a YouTube link is put in
    /// the box as typed (the box then says what is wrong); a drop without one says so.
    /// </summary>
    internal void HandleIncomingText(string? text, bool fromDrop)
    {
        var links = LinkExtractor.Extract(text);
        var videos = links.Where(l => l.Kind == LinkKind.Video).ToList();
        var playlists = links.Where(l => l.Kind == LinkKind.Playlist).ToList();

        if (links.Count == 0)
        {
            if (fromDrop) UrlInput.ShowNotice("沒有找到 YouTube 影片或播放清單的網址");
            else if (!string.IsNullOrWhiteSpace(text)) UrlInput.SetTextProgrammatically(text);
            return;
        }
        if (videos.Count == 0)
        {
            _ = OpenPlaylistAsync(playlists[0].Url);
            return;
        }
        if (videos.Count == 1 && playlists.Count == 0)
        {
            var v = videos[0];
            UrlInput.SetTextProgrammatically(v.PlaylistId is null ? v.Url : $"{v.Url}&list={v.PlaylistId}");
            return;
        }
        OpenBatch(
            videos.Select(v => new BatchItem(v.Url, v.Url, "", true)).ToList(),
            $"加入 {videos.Count} 部影片",
            playlists.Count > 0 ? $"另有 {playlists.Count} 個播放清單網址沒有列入，請單獨貼上。" : null);
    }

    /// <summary>
    /// v1.5.0: read a playlist's videos (one yt-dlp call) and let the user pick. The URL box
    /// owns the read (UrlInputView.ReadPlaylistAsync), so a newer paste, another playlist
    /// or ✕ supersedes it, and a superseded read opens nothing.
    /// </summary>
    internal async Task OpenPlaylistAsync(string playlistUrl)
    {
        if (ViewModel is not { } vm) return;
        try
        {
            var result = await UrlInput.ReadPlaylistAsync(playlistUrl);
            if (result is null) return;
            if (result.NotAPlaylist)
            {
                UrlInput.ShowNotice("這個清單無法展開成影片列表（例如沒有可播放內容的自動合輯）");
                return;
            }
            if (!result.IsSuccess || result.Playlist is null)
            {
                var error = ErrorMapper.Map(result.ErrorStderr ?? "");
                vm.Host.Logger.Warn("playlist.failed", new Dictionary<string, string>
                {
                    ["url_hash"]   = AppLogger.HashSuffix(playlistUrl),
                    ["error_code"] = error.ErrorCode,
                });
                UrlInput.ShowNotice("無法讀取這個播放清單：" + error.UserMessage);
                // The same offer a pasted video gets; App asks once per session.
                if (MainViewModel.ShouldOfferSslPrompt(error))
                    App.OfferSslBypassPrompt(vm.Host, "讀取播放清單失敗：" + error.UserMessage);
                // v1.5.1: same as a video whose parse failed because yt-dlp cannot start.
                if (ErrorMapper.CallsForRepair(error)) UrlInput.OfferRepair();
                return;
            }

            // A playlist can list a video twice; one row each would be two jobs writing
            // the same files.
            var entries = result.Playlist.Entries.DistinctBy(e => e.VideoId).ToList();
            vm.Host.Logger.Info("playlist.opened", new Dictionary<string, string>
            {
                ["url_hash"] = AppLogger.HashSuffix(playlistUrl),
                ["entries"]  = entries.Count.ToString(),
            });
            if (entries.Count == 0)
            {
                UrlInput.ShowNotice("這個播放清單裡沒有影片");
                return;
            }

            var items = entries
                .Select(e => new BatchItem(e.Url, e.Title, DetailOf(e), e.IsAvailable))
                .ToList();
            OpenBatch(items,
                string.IsNullOrWhiteSpace(result.Playlist.Title) ? "播放清單" : result.Playlist.Title,
                result.Playlist.Entries.Count >= Process.YtDlpRunner.MaxPlaylistEntries
                    ? $"這個播放清單很長，只列出前 {Process.YtDlpRunner.MaxPlaylistEntries} 部影片。"
                    : null);
        }
        catch (Exception ex)
        {
            // Called fire-and-forget from clicks and drops: never let it take the app down.
            vm.Host.Logger.Warn("playlist.failed", new Dictionary<string, string>
            {
                ["url_hash"]   = AppLogger.HashSuffix(playlistUrl),
                ["error_code"] = ex.GetType().Name,
            });
            UrlInput.ShowNotice("無法讀取這個播放清單：" + ex.Message);
        }
    }

    private static string DetailOf(PlaylistEntry e) => e.State switch
    {
        PlaylistEntryState.Live => "直播中，無法下載",
        PlaylistEntryState.Upcoming => "尚未開始",
        PlaylistEntryState.Unavailable => "無法下載",
        _ => e.Duration is { } d ? MainViewModel.FormatDuration(d) : "",
    };

    private void OpenBatch(IReadOnlyList<BatchItem> items, string header, string? extraNote, DownloadMode? mode = null)
    {
        if (ViewModel is not { } vm) return;
        var note = "儲存到 " + SaveLocationView.FriendlyPath(vm.SaveDirectory);
        if (extraNote is not null) note = extraNote + "\n" + note;
        var selection = BatchDialog.Ask(this, header, items, mode ?? vm.SelectedMode,
            vm.Host.Config.LastVideoQuality, vm.Host.Config.LastAudioQuality, note);
        if (selection is not null) vm.AddBatch(selection);
    }

    // Hides the overlay once drag events stop arriving: DragLeave is also raised when the
    // pointer merely crosses into a child element, and a drag cancelled with Esc over the
    // window ends with a DragLeave whose position is still inside it.
    private System.Windows.Threading.DispatcherTimer? _dropOverlayTimer;

    private void KeepDropOverlay(bool show)
    {
        _dropOverlayTimer ??= CreateDropOverlayTimer();
        _dropOverlayTimer.Stop();
        DropOverlay.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (show) _dropOverlayTimer.Start();
    }

    private System.Windows.Threading.DispatcherTimer CreateDropOverlayTimer()
    {
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        timer.Tick += (_, _) => { timer.Stop(); DropOverlay.Visibility = Visibility.Collapsed; };
        return timer;
    }

    // Decided when a drag enters and reused for its DragOver events (several a second):
    // nothing here touches the disk, which on a redirected desktop means the network.
    private bool _dragHasLinks;

    private void OnPreviewDragEnter(object sender, DragEventArgs e)
    {
        _dragHasLinks = HasLinkData(e.Data);
        OnPreviewDragOver(sender, e);
    }

    private void OnPreviewDragOver(object sender, DragEventArgs e)
    {
        // A modal dialog is up (加入多部影片, 設定, a prompt): a drop would start work behind it.
        var accept = _dragHasLinks && !System.Windows.Interop.ComponentDispatcher.IsThreadModal;
        // Always Copy, never Move: a Move would tell an editor to delete the dragged text.
        e.Effects = accept ? DragDropEffects.Copy : DragDropEffects.None;
        KeepDropOverlay(accept);
        // Handled on the way down, so the URL box does not also take the text itself.
        e.Handled = true;
    }

    private void OnPreviewDragLeave(object sender, DragEventArgs e)
    {
        // Let the timer decide: another DragOver right away means the pointer only moved
        // onto a child element.
        _dropOverlayTimer?.Stop();
        _dropOverlayTimer?.Start();
    }

    private void OnPreviewDrop(object sender, DragEventArgs e)
    {
        KeepDropOverlay(false);
        e.Handled = true;
        if (!_dragHasLinks || System.Windows.Interop.ComponentDispatcher.IsThreadModal)
        {
            e.Effects = DragDropEffects.None;
            return;
        }
        e.Effects = DragDropEffects.Copy;
        var text = ReadDroppedText(e.Data);
        Activate();
        // After the drop returns: the drag source (a browser) is blocked inside its own
        // DoDragDrop until then, and a playlist or batch opens a modal dialog.
        Dispatcher.BeginInvoke(() => HandleIncomingText(text, fromDrop: true));
    }

    private static bool HasLinkData(IDataObject data)
    {
        try
        {
            if (data.GetDataPresent(DataFormats.UnicodeText) || data.GetDataPresent(DataFormats.Text) ||
                data.GetDataPresent("UniformResourceLocatorW"))
                return true;
            // By name only: checking the files themselves would hit the disk on every drag event.
            return data.GetData(DataFormats.FileDrop) is string[] files && files.Any(HasLinkFileName);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or OutOfMemoryException)
        {
            return false;
        }
    }

    // Everything the drop carries, joined: text, a browser's link format, and .url / .txt
    // files — plain text without a link must not hide the link next to it. Each source is
    // read on its own, so one unreadable file does not lose the rest.
    private static string? ReadDroppedText(IDataObject data)
    {
        var parts = new List<string>();
        void Try(Func<string?> read)
        {
            try
            {
                if (read() is { Length: > 0 } s) parts.Add(s);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or System.Runtime.InteropServices.COMException)
            {
            }
        }

        Try(() => data.GetData(DataFormats.UnicodeText) as string ?? data.GetData(DataFormats.Text) as string);
        Try(() => data.GetData("UniformResourceLocatorW") is MemoryStream url
            ? Encoding.Unicode.GetString(url.ToArray()).TrimEnd('\0')
            : null);
        string[]? files = null;
        Try(() => { files = data.GetData(DataFormats.FileDrop) as string[]; return null; });
        foreach (var file in files ?? Array.Empty<string>())
        {
            if (!HasLinkFileName(file)) continue;
            Try(() => File.Exists(file) && new FileInfo(file).Length < 1_000_000 ? File.ReadAllText(file) : null);
        }
        return parts.Count == 0 ? null : string.Join("\n", parts);
    }

    private static bool HasLinkFileName(string path) =>
        path.EndsWith(".url", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".txt", StringComparison.OrdinalIgnoreCase);

    private void OnSettingsClicked(object sender, RoutedEventArgs e)
    {
        var host = ((App)Application.Current).Host;
        if (host is null) return;
        var dlg = new SettingsDialog(host) { Owner = this };
        dlg.ShowDialog();
        // The default folder may have changed; keep the composer's 儲存到 in step.
        if (ViewModel is not null) ViewModel.SaveDirectory = host.Config.DefaultSaveDirectory;
    }

    private void OnOpenFolderClicked(object sender, RoutedEventArgs e)
    {
        // UseShellExecute=true so Windows treats this like a normal explorer.exe launch.
        var host = ((App)Application.Current).Host;
        if (host is null) return;
        var dir = ViewModel?.SaveDirectory ?? host.Config.DefaultSaveDirectory;
        try
        {
            Directory.CreateDirectory(dir);
            // Fully qualified: the project's own YtDlpTool.Process namespace shadows "Process".
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", dir)
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            AppDialog.Show(this, $"無法開啟資料夾：{ex.Message}", "錯誤", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnAddDownloadClicked(object sender, RoutedEventArgs e)
    {
        ViewModel?.AddDownloadCommand.Execute(null);
        // The next thing people do is paste another link: leave the URL box focused with
        // its text selected so Ctrl+V replaces it.
        UrlInput.FocusUrlBox(selectAll: true);
    }

    public ICommand PasteFromClipboardCommand => new RelayCommandAdapter(_ =>
    {
        string? text = null;
        try { if (Clipboard.ContainsText()) text = Clipboard.GetText(); }
        catch (System.Runtime.InteropServices.ExternalException) { /* clipboard held by another app */ }
        if (text is not null) HandleIncomingText(text, fromDrop: false);
    });

    public ICommand CancelSelectedQueueCommand => new RelayCommandAdapter(_ =>
    {
        if (ViewModel?.SelectedQueueItem is { } item)
        {
            if (item.IsActive) item.MarkCancelling();
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

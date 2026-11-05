using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using YtDlpTool.Domain.Models;
using YtDlpTool.Domain.Services;

namespace YtDlpTool.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly AppHost _host;

    public MainViewModel(AppHost host)
    {
        _host = host;
        SaveDirectory = host.Config.DefaultSaveDirectory;
        // v1.5.0: start in the 格式 of the last download.
        SelectedMode = host.Config.LastMode ?? DownloadMode.AudioAndVideo;
        Queue = new ObservableCollection<QueueItemViewModel>();
        host.QueueEventRaised += OnQueueEvent;
    }

    /// <summary>v1.5.0: the remembered 畫質 (video modes) or 音質 (audio) for <paramref name="mode"/>.</summary>
    public string? PreferredQuality(DownloadMode mode) =>
        mode == DownloadMode.AudioOnly ? _host.Config.LastAudioQuality : _host.Config.LastVideoQuality;

    /// <summary>v1.5.0: remember 格式 + 畫質 for the next video and as the playlist/batch default.</summary>
    internal void RememberChoice(DownloadMode mode, string quality)
    {
        var cfg = _host.Config;
        cfg.LastMode = mode;
        if (mode == DownloadMode.AudioOnly) cfg.LastAudioQuality = quality;
        else cfg.LastVideoQuality = quality;
        // A convenience: a config file that cannot be written must not block a download.
        try { _host.ConfigStore.Save(cfg); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAddDownload))]
    private string _saveDirectory = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAddDownload), nameof(HasMetadata), nameof(MetaSubtitle),
                              nameof(HasPlaylist), nameof(CurrentPlaylistUrl))]
    private VideoMetadata? _currentMetadata;

    // v1.5.0: what VideoCardView binds to.
    [ObservableProperty] private ImageSource? _currentThumbnail;

    /// <summary>
    /// v1.5.0: the list= of the link the current video came from (watch?v=…&amp;list=…), so
    /// the card can offer the rest of that playlist. Null otherwise.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPlaylist), nameof(CurrentPlaylistUrl))]
    private string? _currentPlaylistId;

    /// <summary>
    /// Where the card's 「選擇清單裡的影片…」 reads that list: the watch page, which also works
    /// for Mix lists; null for lists that need sign-in (PlaylistLinks).
    /// </summary>
    public string? CurrentPlaylistUrl => PlaylistLinks.ForVideo(CurrentMetadata?.VideoId, CurrentPlaylistId);

    public bool HasPlaylist => CurrentPlaylistUrl is not null;

    public bool HasMetadata => CurrentMetadata is not null;

    /// <summary>「頻道 · 3:25」 under the title in the video card.</summary>
    public string MetaSubtitle => CurrentMetadata is null
        ? ""
        : string.IsNullOrWhiteSpace(CurrentMetadata.Channel)
            ? FormatDuration(CurrentMetadata.Duration)
            : $"{CurrentMetadata.Channel} · {FormatDuration(CurrentMetadata.Duration)}";

    internal static string FormatDuration(TimeSpan d) =>
        d.TotalHours >= 1 ? $"{(int)d.TotalHours}:{d.Minutes:00}:{d.Seconds:00}" : $"{d.Minutes}:{d.Seconds:00}";

    [ObservableProperty] private DownloadMode _selectedMode = DownloadMode.AudioAndVideo;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAddDownload))]
    private VideoFormat? _selectedFormat;

    [ObservableProperty] private TimeRange? _clipRange;
    [ObservableProperty] private bool _isParsing;
    [ObservableProperty] private string? _parseError;
    [ObservableProperty] private QueueItemViewModel? _selectedQueueItem;

    public ObservableCollection<string> SelectedSubtitleLanguages { get; } = new();
    public ObservableCollection<QueueItemViewModel> Queue { get; }
    public AppHost Host => _host;

    public bool CanAddDownload =>
        CurrentMetadata is not null && SelectedFormat is not null && !string.IsNullOrEmpty(SaveDirectory);

    [RelayCommand]
    private void AddDownload()
    {
        if (!CanAddDownload || CurrentMetadata is null || SelectedFormat is null) return;
        var job = new DownloadJob(
            url: $"https://www.youtube.com/watch?v={CurrentMetadata.VideoId}",
            title: CurrentMetadata.Title,
            thumbnailUrl: CurrentMetadata.ThumbnailUrl,
            mode: SelectedMode,
            chosenFormat: SelectedFormat,
            subtitleLanguageCodes: SelectedSubtitleLanguages.ToArray(),
            clipRange: ClipRange,
            saveDirectory: SaveDirectory);
        _host.Queue.Enqueue(job);
        RememberChoice(SelectedMode, QualityOptions.ShortLabel(SelectedMode, SelectedFormat));
        // Intentionally do NOT clear URL/Format/Clip/Subs here — the user has just spent
        // effort dialling these in and frequently wants to enqueue the same video again
        // with a tweaked option (different quality, different clip). The reset happens
        // automatically in OnCurrentMetadataChanged when the URL resolves to a different
        // VideoId. See Fix 5 + Fix 6.
    }

    /// <summary>
    /// Reset video-specific options when the URL resolves to a different video. We compare
    /// VideoIds rather than raw URLs so a youtu.be link and the youtube.com equivalent
    /// don't trigger a spurious reset. Transitions null→meta (first paste) and meta→null
    /// (clear/error) leave the user's last-selected options alone.
    /// </summary>
    partial void OnCurrentMetadataChanged(VideoMetadata? oldValue, VideoMetadata? newValue)
    {
        // v1.5.0: another video's picture must not stay beside this title when this one's
        // thumbnail fails to load (it was only ever replaced on success).
        if (newValue is not null && oldValue?.VideoId != newValue.VideoId) CurrentThumbnail = null;
        if (!MetadataChangePolicy.ShouldResetOptions(oldValue, newValue)) return;
        SelectedFormat = null;
        ClipRange = null;
        SelectedSubtitleLanguages.Clear();
    }

    [RelayCommand]
    private void CancelJob(Guid id)
    {
        var item = Queue.FirstOrDefault(q => q.Id == id);
        if (item is null) return;
        if (item.Status is JobStatus.Completed or JobStatus.Failed or JobStatus.Cancelled)
        {
            // Already terminal — the row is now a 'remove from list' affordance.
            Queue.Remove(item);
            RefreshTaskbar();
        }
        else
        {
            // Active job — kick off cancellation; OnQueueEvent will remove the row
            // once JobCancelledEvent propagates back from the queue.
            item.MarkCancelling();
            _host.Queue.Cancel(id);
        }
    }

    // Rows waiting for their retried job's JobEnqueuedEvent. UI thread only.
    private readonly Dictionary<Guid, QueueItemViewModel> _retryRows = new();

    /// <summary>v1.5.0: 「重試」 on a failed row — the same request again, in the same row.</summary>
    [RelayCommand]
    private void RetryJob(Guid id)
    {
        var item = Find(id);
        if (item?.Job is not { } failed || !item.IsFailed) return;
        // Leave the failed state at once: a second click (the row only restarts once the
        // queue's event arrives) would otherwise queue a second copy into the same row.
        item.SetStatus(JobStatus.Pending);
        // The failed run counted as done on the taskbar; the retry starts from zero.
        _taskbar.Forget(failed.Id);
        var again = failed.CloneForRetry();
        _retryRows[again.Id] = item;
        _host.Queue.Enqueue(again);
        RefreshTaskbar();
    }

    /// <summary>v1.5.0: 「在資料夾中顯示」 on a finished row — opens Explorer with the file selected.</summary>
    [RelayCommand]
    private void OpenItemFolder(Guid id)
    {
        var path = Queue.FirstOrDefault(q => q.Id == id)?.OutputFilePath;
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            // Fully qualified: YtDlpTool.Process (the project namespace) shadows "Process".
            var psi = File.Exists(path)
                ? new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{path}\"")
                : new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{Path.GetDirectoryName(path)}\"");
            psi.UseShellExecute = true;
            System.Diagnostics.Process.Start(psi);
        }
        catch { /* best effort — Explorer not starting is not worth an error dialog */ }
    }

    // v1.5.0: offered only when something finished (it was enabled for any non-empty list).
    [RelayCommand(CanExecute = nameof(HasFinishedRows))]
    private void RemoveCompleted()
    {
        var terminal = Queue.Where(q =>
            q.Status is JobStatus.Completed or JobStatus.Failed or JobStatus.Cancelled).ToList();
        foreach (var item in terminal) Queue.Remove(item);
        RefreshTaskbar();
    }

    private bool HasFinishedRows() =>
        Queue.Any(q => q.Status is JobStatus.Completed or JobStatus.Failed or JobStatus.Cancelled);

    // ── v1.5.0: taskbar progress ───────────────────────────────────────────────

    private readonly TaskbarProgressTracker _taskbar = new();
    [ObservableProperty] private TaskbarState _taskbarProgressState;
    [ObservableProperty] private double _taskbarProgressValue;

    // Every change of a row's status passes through here, so 清除已完成 follows it too.
    private void RefreshTaskbar()
    {
        var (state, value) = _taskbar.Update(Queue.Select(q => new QueueRow(q.Id, q.Status, q.ProgressPercent)));
        TaskbarProgressState = state;
        TaskbarProgressValue = value;
        RemoveCompletedCommand.NotifyCanExecuteChanged();
    }

    // ── v1.5.0: playlists and several links at once ────────────────────────────

    // The loop lives in Domain (BatchQueue) so de-duplication, the failure summary and the
    // stop rules are unit-tested; this side supplies the parse-and-queue step. It all runs
    // on the UI thread: Add is called there and nothing on the way leaves its context.
    private BatchQueue? _batch;
    private BatchQueue Batch => _batch ??= CreateBatch();
    private readonly BatchDownloadTally _batchTally = new();
    private bool _batchWasRunning;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDismissBatchStatus))]
    private bool _isBatchRunning;

    [ObservableProperty] private bool _showBatchStatus;

    /// <summary>The status line's ✕ — offered once nothing is being added any more.</summary>
    public bool CanDismissBatchStatus => !IsBatchRunning;
    [ObservableProperty] private string _batchStatus = "";
    [ObservableProperty] private double _batchPercent;

    private BatchQueue CreateBatch()
    {
        var batch = new BatchQueue(QueueOneAsync, IsAlreadyQueued);
        batch.Changed += OnBatchChanged;
        // Links not queued yet when the app closes come back at the next start.
        BatchJournal.Attach(batch, _host.StateJournal);
        return batch;
    }

    /// <summary>
    /// v1.5.0: queue every link of a playlist or a multi-link paste. Each one is parsed like
    /// a pasted link, one at a time, and gets the chosen quality or the nearest lower one
    /// that video has. A second batch added while one is running joins it.
    /// </summary>
    public void AddBatch(BatchSelection selection)
    {
        if (selection.Urls.Count == 0) return;
        RememberChoice(selection.Mode, selection.Quality);
        SelectedMode = selection.Mode;
        ShowBatchStatus = true;
        // One id per confirmed list: 「檔案已存在」 can then be answered for all of it.
        var batchId = Guid.NewGuid();
        Batch.Add(selection.Urls.Select(url => new BatchWork(url, selection.Mode, selection.Quality) { BatchId = batchId }));
    }

    [RelayCommand]
    private void StopBatch() => _batch?.Stop();

    [RelayCommand]
    private void DismissBatchStatus()
    {
        if (!IsBatchRunning) ShowBatchStatus = false;
    }

    private void OnBatchChanged()
    {
        var p = Batch.Progress;
        IsBatchRunning = p.IsRunning;
        BatchPercent = p.Total == 0 ? 0 : 100.0 * p.Done / p.Total;
        BatchStatus = BatchStatusText.Format(p);
        if (p.IsRunning && !_batchWasRunning) _batchTally.AddingStarted();
        if (!p.IsRunning && _batchWasRunning && _batchTally.AddingFinished() is { } summary)
            Interop.ToastService.NotifyBatchFinished(summary.Completed, summary.Failed);
        _batchWasRunning = p.IsRunning;
    }

    // Two jobs for one video would write the same files at the same time.
    private bool IsAlreadyQueued(string url) => Queue.Any(q => q.IsActive && q.Job?.Url == url);

    private async Task<BatchItemResult> QueueOneAsync(BatchWork work, CancellationToken ct)
    {
        // The once-per-launch cold start, exactly as a pasted link waits for it.
        await _host.EnsureYtDlpReadyAsync().WaitAsync(ct);
        var result = await _host.YtDlp.FetchMetadataAsync(work.Url, ct);
        ct.ThrowIfCancellationRequested();
        if (!result.IsSuccess || result.Metadata is null)
        {
            var error = ErrorMapper.Map(result.ErrorStderr ?? "");
            _host.Logger.Warn("batch.item.failed", new Dictionary<string, string>
            {
                ["url_hash"]   = Domain.Logging.AppLogger.HashSuffix(work.Url),
                ["error_code"] = error.ErrorCode,
            });
            // The same offer a single pasted link gets. Behind an HTTPS-inspecting proxy a
            // playlist is often the first thing that fails; App asks once per session.
            if (ShouldOfferSslPrompt(error))
                App.OfferSslBypassPrompt(_host, "加入播放清單裡的影片時失敗：" + error.UserMessage);
            return BatchItemResult.Failed(error);
        }

        var meta = result.Metadata;
        var choices = QualityOptions.Build(work.Mode, meta.Formats);
        var index = QualityOptions.PreferredIndex(choices, work.Quality);
        if (index < 0)
        {
            _host.Logger.Warn("batch.item.failed", new Dictionary<string, string>
            {
                ["url_hash"]   = Domain.Logging.AppLogger.HashSuffix(work.Url),
                ["error_code"] = "no_formats",
            });
            return BatchItemResult.Failed(new MappedError(ErrorCategory.VideoUnavailable,
                "這部影片沒有可下載的格式", "E-NOFMT", false));
        }

        var job = new DownloadJob(
            url: $"https://www.youtube.com/watch?v={meta.VideoId}",
            title: meta.Title,
            thumbnailUrl: meta.ThumbnailUrl,
            mode: work.Mode,
            chosenFormat: choices[index].Format,
            subtitleLanguageCodes: Array.Empty<string>(),
            clipRange: null,
            saveDirectory: SaveDirectory)
        {
            BatchId = work.BatchId,
        };
        _batchTally.Expect(job.Id);
        _host.Queue.Enqueue(job);
        return BatchItemResult.Added;
    }

    // v1.5.0: at most four progress updates a second per job reach the UI.
    private readonly ProgressThrottle _progressThrottle = new(TimeSpan.FromMilliseconds(250));

    private void OnQueueEvent(object? sender, QueueEvent evt)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null) return;

        // Dropped on the worker thread, before anything is queued for the UI. Not a switch
        // to 處理中: a handful per download, and dropping the first leaves a frozen speed.
        if (evt is JobProgressEvent p && !p.Progress.Processing &&
            !_progressThrottle.ShouldPublish(p.Job.Id, p.Progress.Percent, DateTime.UtcNow))
            return;

        // v1.5.0: BeginInvoke, not Invoke. Invoke parked the download worker until the UI
        // thread got round to it — on every progress line. Same priority keeps the
        // events in order (Enqueued before Started before Progress before Completed).
        dispatcher.BeginInvoke(() =>
        {
            switch (evt)
            {
                case JobEnqueuedEvent e:
                    if (_retryRows.Remove(e.Job.Id, out var retried))
                    {
                        retried.Restart(e.Job);
                        break;
                    }
                    var newItem = new QueueItemViewModel
                    {
                        Id = e.Job.Id,
                        Job = e.Job,
                        Title = e.Job.Title,
                        ThumbnailUrl = e.Job.ThumbnailUrl,
                        Status = JobStatus.Pending,
                        ModeLabel = ModeLabel(e.Job.Mode),
                        QualityLabel = QualityOptions.ShortLabel(e.Job.Mode, e.Job.ChosenFormat)
                    };
                    Queue.Add(newItem);
                    // Load the thumbnail in-memory so the queue row's <Image> never has
                    // to round-trip through WinINet (which would write to INetCache).
                    _ = LoadQueueThumbnailAsync(newItem, e.Job.ThumbnailUrl);
                    break;
                case JobStartedEvent e:
                    Find(e.Job.Id)?.SetStatus(JobStatus.Downloading);
                    break;
                case JobProgressEvent e:
                    Find(e.Job.Id)?.UpdateProgress(e.Progress.Percent, e.Progress.BytesPerSecond, e.Progress.Eta,
                        e.Progress.Processing);
                    break;
                case JobCompletedEvent e:
                    _progressThrottle.Forget(e.Job.Id);
                    var c = Find(e.Job.Id);
                    if (c is not null)
                    {
                        c.SetStatus(JobStatus.Completed);
                        c.OutputFilePath = e.OutputFilePath;
                        c.ProgressPercent = 100;
                    }
                    // v1.5.0: a batch's downloads report once, together (BatchDownloadTally).
                    if (!_batchTally.IsBatchJob(e.Job.Id))
                        Interop.ToastService.NotifyDownloadCompleted(e.Job.Title, e.OutputFilePath);
                    else if (_batchTally.Finish(e.Job.Id, succeeded: true) is { } done)
                        Interop.ToastService.NotifyBatchFinished(done.Completed, done.Failed);
                    break;
                case JobFailedEvent e:
                    _progressThrottle.Forget(e.Job.Id);
                    var f = Find(e.Job.Id);
                    if (f is not null)
                    {
                        f.SetStatus(JobStatus.Failed);
                        f.FailureReason = e.Error.UserMessage;
                    }
                    if (!_batchTally.IsBatchJob(e.Job.Id))
                        Interop.ToastService.NotifyDownloadFailed(e.Job.Title, e.Error.UserMessage);
                    else if (_batchTally.Finish(e.Job.Id, succeeded: false) is { } ended)
                        Interop.ToastService.NotifyBatchFinished(ended.Completed, ended.Failed);
                    // v1.3.0/1.3.3: if this failure looks SSL-related and the user
                    // hasn't already opted into the bypass, offer it now with full
                    // security context. ShouldOfferSslPrompt avoids mis-routing
                    // non-SSL timeouts (e.g. yt-dlp launch failures) into the SSL
                    // prompt by checking RawDetails for SSL hints.
                    if (ShouldOfferSslPrompt(e.Error))
                    {
                        App.OfferSslBypassPrompt(_host,
                            $"下載「{e.Job.Title}」失敗：{e.Error.UserMessage}");
                    }
                    break;
                case JobCancelledEvent e:
                    _progressThrottle.Forget(e.Job.Id);
                    if (_batchTally.Forget(e.Job.Id) is { } rest)
                        Interop.ToastService.NotifyBatchFinished(rest.Completed, rest.Failed);
                    var x = Find(e.Job.Id);
                    if (x is not null)
                    {
                        x.SetStatus(JobStatus.Cancelled);
                        // User clicked ✕ on an active row — surface this as 'gone from the
                        // list' so the affordance behaves the same as removing a terminal item.
                        Queue.Remove(x);
                    }
                    break;
            }
            RefreshTaskbar();
        });
    }

    private QueueItemViewModel? Find(Guid id) => Queue.FirstOrDefault(q => q.Id == id);

    // v1.3.3: rather than naive blanket "prompt SSL on any network-ish failure",
    // gate the offer on actual evidence that SSL is involved. v1.3.2 broadened
    // the trigger to E-TIMEOUT01 and ended up mis-routing zero-output timeouts
    // (yt-dlp.exe hung during PyInstaller cold-start under AV scanning) into
    // the SSL prompt, which is unhelpful and confusing.
    //
    //   - E-SSL01 / E-SSL02 → always offer (direct SSL evidence)
    //   - E-TIMEOUT01 + RawDetails contains "ssl"/"certificate"/"verify" →
    //     offer (SSL failure surfaced as connect-timeout)
    //   - E-TIMEOUT02 (zero-output timeout, new in v1.3.3) → never offer;
    //     points at AV/launch/resource problem, not SSL
    //   - Anything else → don't offer; user can still flip the flag manually
    internal static bool ShouldOfferSslPrompt(Domain.Services.MappedError err)
    {
        if (err.ErrorCode == "E-SSL01" || err.ErrorCode == "E-SSL02") return true;
        if (err.ErrorCode == "E-TIMEOUT01" && HasSslHint(err.RawDetails)) return true;
        return false;
    }

    private static bool HasSslHint(string? raw) =>
        !string.IsNullOrEmpty(raw) && (
            raw.Contains("ssl",         StringComparison.OrdinalIgnoreCase) ||
            raw.Contains("certificate", StringComparison.OrdinalIgnoreCase) ||
            raw.Contains("verify",      StringComparison.OrdinalIgnoreCase));

    private static async Task LoadQueueThumbnailAsync(QueueItemViewModel item, string url)
    {
        // 112 px wide on screen; 224 keeps it sharp at 200 % scaling. A full-size frame per
        // row was ~3.7 MB, which a 500-video playlist turns into gigabytes.
        var bmp = await Interop.InMemoryThumbnailLoader.LoadAsync(url, maxPixelWidth: 224).ConfigureAwait(true);
        if (bmp is not null) item.ThumbnailImage = bmp;
    }

    // v1.5.0: the same names as the 格式 selector (影片 / 音訊 / 無聲影片).
    private static string ModeLabel(DownloadMode m) => m switch
    {
        DownloadMode.AudioOnly => "音訊",
        DownloadMode.VideoOnly => "無聲影片",
        DownloadMode.AudioAndVideo => "影片",
        _ => ""
    };
}

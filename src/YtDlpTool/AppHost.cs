using System.IO;
using YtDlpTool.Domain.Logging;
using YtDlpTool.Domain.Models;
using YtDlpTool.Domain.Persistence;
using YtDlpTool.Domain.Security;
using YtDlpTool.Domain.Services;
using YtDlpTool.Domain.Updates;
using YtDlpTool.Process;
using YtDlpTool.ViewModels;

namespace YtDlpTool;

public sealed class AppHost : IDisposable
{
    public AppPaths Paths { get; }
    public AppConfig Config { get; private set; }
    public ConfigStore ConfigStore { get; }
    public AppLogger Logger { get; }
    public StateJournal StateJournal { get; }
    public YtDlpRunner YtDlp { get; }
    public FfmpegRunner Ffmpeg { get; }
    public HttpUpdateClient UpdateHttp { get; }
    public UpdateChecker UpdateChecker { get; }
    public UpdateApplier UpdateApplier { get; }
    public DownloadQueue Queue { get; }

    public AppHost()
    {
        Paths = AppPaths.ResolveForCurrentProcess();
        Paths.EnsureDataDirectoriesExist();

        ConfigStore = new ConfigStore(Paths.ConfigFile);
        Config = ConfigStore.Load();
        if (string.IsNullOrWhiteSpace(Config.DefaultSaveDirectory))
        {
            Config.DefaultSaveDirectory = AppConfig.CreateDefault().DefaultSaveDirectory;
            ConfigStore.Save(Config);
        }
        // NOTE: do not pre-create Config.DefaultSaveDirectory here — that left an empty
        // YtDlpTool folder in the user's Downloads even when they never queued a job.
        // The directory is created just-in-time by YtDlpDownloadExecutor before the
        // first download into that location.

        Logger = new AppLogger(
            Paths.LogsDirectory,
            ParseLogLevel(Config.LogLevel),
            () => DateTime.Now);
        AppLogger.PurgeOlderThan(Paths.LogsDirectory, TimeSpan.FromDays(7), DateTime.Now);

        // Generate a CA bundle from Windows' trust store and expose it via SSL_CERT_FILE.
        // This is THE fix for managed environments with SSL inspection: yt-dlp's bundled
        // Python certifi doesn't know about the site-installed CA installed via GPO into
        // Windows' root store. Without this, yt-dlp's HTTPS handshake fails silently
        // and the metadata fetch hangs until our 30s timeout while the browser on the
        // same machine works fine (because browsers trust the Windows store directly).
        var caBundlePath = Path.Combine(Paths.DataRoot, "system-ca-bundle.pem");
        if (SystemCertBundle.GenerateOrRefresh(caBundlePath))
        {
            // Setting it on the parent process means ProcessSandbox's existing
            // SSL_CERT_FILE pass-through (added in v1.1.14) auto-propagates to yt-dlp.
            Environment.SetEnvironmentVariable("SSL_CERT_FILE", caBundlePath);
            Logger.Info("ca-bundle.generated", new Dictionary<string, string>
            {
                ["path"]   = caBundlePath,
                ["bytes"]  = new FileInfo(caBundlePath).Length.ToString(),
            });
        }
        else
        {
            Logger.Warn("ca-bundle.generation_failed", new Dictionary<string, string>
            {
                ["path"] = caBundlePath
            });
        }

        StateJournal = new StateJournal(Paths.StateLog);

        var ytDlpExe  = Path.Combine(Paths.BinDirectory, "yt-dlp.exe");
        var ffmpegExe = Path.Combine(Paths.BinDirectory, "ffmpeg.exe");
        YtDlp  = new YtDlpRunner(ytDlpExe, allowUntrustedCerts: Config.AllowUntrustedCertificates);
        Ffmpeg = new FfmpegRunner(ffmpegExe);

        UpdateHttp = new HttpUpdateClient($"YtDlpTool/{ThisVersion()}");

        var sigstoreOpts = new SigstoreVerifierOptions(
            ExpectedIssuer: "https://token.actions.githubusercontent.com",
            ExpectedSanRegex: @"^https://github\.com/AwesomeBigBoy/YTDownloadTool/\.github/workflows/release\.yml@refs/tags/v.*$",
            TrustedRootPem: SigstoreRoots.FulcioRootPem);

        // Fix 2 (v1.1.6): pass AppLogger so the checker writes update.check.* events
        // for each fallback step. Lets the user diagnose "找不到最新版本" via the
        // 顯示診斷詳情 link in Settings → 更新.
        UpdateChecker = new UpdateChecker(UpdateHttp, sigstoreOpts,
            owner: "AwesomeBigBoy", repo: "YTDownloadTool", logger: Logger);
        UpdateApplier = new UpdateApplier(UpdateHttp, sigstoreOpts, Paths, Logger);

        var executor = new YtDlpDownloadExecutor(YtDlp, Ffmpeg, Logger);
        var journaledOnEvent = JournaledQueue.Wrap(StateJournal, OnQueueEvent);
        Queue = new DownloadQueue(executor, Config.ConcurrentDownloads, journaledOnEvent, Logger);

        // Fix D + WinHTTP fallback: log once at startup whether a system proxy was
        // detected. We hash the proxy URL rather than logging it raw to avoid
        // leaking internal host names (per the same privacy convention used for
        // job URLs). Detection now covers explicit ProxyServer registry entries
        // AND WPAD/PAC auto-resolution via WinHTTP for domain-joined hosts.
        var detectedProxy = SystemProxy.DetectHttpProxy();
        Logger.Info("system.proxy.detected", new Dictionary<string, string>
        {
            ["status"] = detectedProxy is null ? "none" : "detected",
            ["proxy_host_hash"] = detectedProxy is null ? "" : AppLogger.HashSuffix(detectedProxy)
        });
    }

    public event EventHandler<QueueEvent>? QueueEventRaised;

    private void OnQueueEvent(QueueEvent evt)
    {
        QueueEventRaised?.Invoke(this, evt);
    }

    private static LogLevel ParseLogLevel(string s) => s switch
    {
        "Debug" => LogLevel.Debug,
        "Info"  => LogLevel.Info,
        "Warn"  => LogLevel.Warn,
        "Error" => LogLevel.Error,
        _       => LogLevel.Info
    };

    private static string ThisVersion() =>
        System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0";

    /// <summary>
    /// Probes installed yt-dlp and ffmpeg versions plus this app's version. Used by both the
    /// background update check and the Settings dialog's manual "check now" / "redownload components"
    /// flows so they share the exact same probe path.
    /// </summary>
    public async Task<InstalledVersions> GetInstalledVersionsAsync() => new(
        App: ThisVersion(),
        YtDlp: await ProbeYtDlpVersionAsync().ConfigureAwait(false),
        Ffmpeg: await ProbeFfmpegVersionAsync().ConfigureAwait(false));

    public UpdateBannerViewModel BannerVm { get; } = new();

    public async Task StartBackgroundUpdateCheckAsync(CancellationToken ct)
    {
        // Wait 60 seconds after startup before first check (spec 4.3).
        try { await Task.Delay(TimeSpan.FromSeconds(60), ct).ConfigureAwait(false); }
        catch (TaskCanceledException) { return; }

        if (!ShouldCheckNow(Config)) return;

        var installed = await GetInstalledVersionsAsync().ConfigureAwait(false);

        var availability = await UpdateChecker.CheckAsync(installed, ct).ConfigureAwait(false);

        if (availability.HasUpdate && availability.NewerFiles.Count > 0)
        {
            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                BannerVm.Entries.Clear();
                foreach (var f in availability.NewerFiles) BannerVm.Entries.Add(f);
                BannerVm.Headline = availability.NewerFiles.Count == 1
                    ? $"有新版本可更新 · {availability.NewerFiles[0].Name} {availability.NewerFiles[0].Version}"
                    : $"有 {availability.NewerFiles.Count} 個元件可更新";
                BannerVm.IsVisible = true;
            });
        }

        Config.LastAppCheck = DateTimeOffset.UtcNow;
        ConfigStore.Save(Config);
    }

    private static bool ShouldCheckNow(AppConfig cfg)
    {
        if (cfg.AppCheckFrequency == UpdateCheckFrequency.Never) return false;
        if (cfg.LastAppCheck is null) return true;
        var elapsed = DateTimeOffset.UtcNow - cfg.LastAppCheck.Value;
        return cfg.AppCheckFrequency switch
        {
            UpdateCheckFrequency.EveryLaunch => true,
            UpdateCheckFrequency.Daily       => elapsed >= TimeSpan.FromDays(1),
            UpdateCheckFrequency.Weekly      => elapsed >= TimeSpan.FromDays(7),
            UpdateCheckFrequency.Monthly     => elapsed >= TimeSpan.FromDays(30),
            _ => false
        };
    }

    private async Task<string> ProbeYtDlpVersionAsync()
    {
        // Probe by running `--version` through ProcessSandbox with a 5-second timeout. Best-effort.
        try
        {
            var args = new ProcessStartArguments(
                ExecutablePath: Path.Combine(Paths.BinDirectory, "yt-dlp.exe"),
                Arguments: new[] { "--version" },
                Timeout: TimeSpan.FromSeconds(5),
                StdoutByteLimit: 64 * 1024);
            var output = new System.Text.StringBuilder();
            var exit = await ProcessSandbox.RunAsync(args, l => { lock (output) output.AppendLine(l.Text); }).ConfigureAwait(false);
            return exit.ExitCode == 0 ? output.ToString().Trim() : "";
        }
        catch { return ""; }
    }

    private async Task<string> ProbeFfmpegVersionAsync()
    {
        try
        {
            var args = new ProcessStartArguments(
                ExecutablePath: Path.Combine(Paths.BinDirectory, "ffmpeg.exe"),
                Arguments: new[] { "-version" },
                Timeout: TimeSpan.FromSeconds(5),
                StdoutByteLimit: 64 * 1024);
            string? firstLine = null;
            var exit = await ProcessSandbox.RunAsync(args, l =>
            {
                if (firstLine is null) firstLine = l.Text;
            }).ConfigureAwait(false);
            if (exit.ExitCode != 0 || string.IsNullOrEmpty(firstLine)) return "";
            // "ffmpeg version 7.1 ..." → take "7.1"
            var parts = firstLine.Split(' ');
            return parts.Length >= 3 ? parts[2] : "";
        }
        catch { return ""; }
    }

    public IReadOnlyList<JobSnapshot> ReadAndClearInterruptedJobs()
    {
        var events = StateJournal.ReadSnapshotAndClear(Paths.StateLog);
        return StateJournal.ReconstructOpenJobs(events).ToList();
    }

    public void Dispose()
    {
        Queue.Dispose();
        UpdateHttp.Dispose();
        StateJournal.Dispose();
        Logger.Dispose();
    }
}

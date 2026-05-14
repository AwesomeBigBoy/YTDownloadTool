using System.IO;
using YtDlpTool.Domain.Logging;
using YtDlpTool.Domain.Models;
using YtDlpTool.Domain.Persistence;
using YtDlpTool.Domain.Security;
using YtDlpTool.Domain.Services;
using YtDlpTool.Domain.Updates;
using YtDlpTool.Process;

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
        Directory.CreateDirectory(Config.DefaultSaveDirectory);

        Logger = new AppLogger(
            Paths.LogsDirectory,
            ParseLogLevel(Config.LogLevel),
            () => DateTime.Now);
        AppLogger.PurgeOlderThan(Paths.LogsDirectory, TimeSpan.FromDays(7), DateTime.Now);

        StateJournal = new StateJournal(Paths.StateLog);

        var ytDlpExe  = Path.Combine(Paths.BinDirectory, "yt-dlp.exe");
        var ffmpegExe = Path.Combine(Paths.BinDirectory, "ffmpeg.exe");
        YtDlp  = new YtDlpRunner(ytDlpExe);
        Ffmpeg = new FfmpegRunner(ffmpegExe);

        UpdateHttp = new HttpUpdateClient($"YtDlpTool/{ThisVersion()}");

        var sigstoreOpts = new SigstoreVerifierOptions(
            ExpectedIssuer: "https://token.actions.githubusercontent.com",
            // Owner/repo filled in by Phase 10 release workflow; here it's a placeholder
            // that will match nothing in dev builds — verifier returns Fail, UpdateChecker swallows it.
            ExpectedSanRegex: @"^https://github\.com/OWNER/REPO/\.github/workflows/release\.yml@refs/tags/v.*$",
            TrustedRootPem: SigstoreRoots.FulcioRootPem);

        UpdateChecker = new UpdateChecker(UpdateHttp, sigstoreOpts, owner: "OWNER", repo: "REPO");
        UpdateApplier = new UpdateApplier(UpdateHttp, sigstoreOpts, Paths);

        var executor = new YtDlpDownloadExecutor(YtDlp);
        var journaledOnEvent = JournaledQueue.Wrap(StateJournal, OnQueueEvent);
        Queue = new DownloadQueue(executor, Config.ConcurrentDownloads, journaledOnEvent);
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

    public void Dispose()
    {
        Queue.Dispose();
        UpdateHttp.Dispose();
        StateJournal.Dispose();
        Logger.Dispose();
    }
}

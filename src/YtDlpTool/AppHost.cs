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

    public UpdateBannerViewModel BannerVm { get; } = new();

    public async Task StartBackgroundUpdateCheckAsync(CancellationToken ct)
    {
        // Wait 60 seconds after startup before first check (spec 4.3).
        try { await Task.Delay(TimeSpan.FromSeconds(60), ct).ConfigureAwait(false); }
        catch (TaskCanceledException) { return; }

        if (!ShouldCheckNow(Config)) return;

        var installed = new InstalledVersions(
            App: ThisVersion(),
            YtDlp: ProbeYtDlpVersion(),
            Ffmpeg: ProbeFfmpegVersion());

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

    private string ProbeYtDlpVersion()
    {
        // Probe by running `--version` synchronously with a tiny timeout. Best-effort.
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = Path.Combine(Paths.BinDirectory, "yt-dlp.exe"),
                Arguments = "--version",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = System.Diagnostics.Process.Start(psi);
            if (p is null) return "";
            if (!p.WaitForExit(2000)) { try { p.Kill(); } catch { } return ""; }
            return p.StandardOutput.ReadToEnd().Trim();
        }
        catch { return ""; }
    }

    private string ProbeFfmpegVersion()
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = Path.Combine(Paths.BinDirectory, "ffmpeg.exe"),
                Arguments = "-version",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = System.Diagnostics.Process.Start(psi);
            if (p is null) return "";
            if (!p.WaitForExit(2000)) { try { p.Kill(); } catch { } return ""; }
            var firstLine = p.StandardOutput.ReadLine() ?? "";
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

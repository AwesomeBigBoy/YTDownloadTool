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

        // v1.2.1: log which config path was actually loaded so we can diagnose path-
        // resolution bugs (e.g. when AppPaths.TestWritable trips on EDR-protected
        // folders and silently routes to %LOCALAPPDATA% instead of the install dir).
        Logger.Info("config.path", new Dictionary<string, string>
        {
            ["path"]   = Paths.ConfigFile,
            ["exists"] = File.Exists(Paths.ConfigFile).ToString(),
            ["root"]   = Paths.DataRoot,
        });
        Logger.Info("config.loaded", new Dictionary<string, string>
        {
            ["ForceSoftwareRendering"]     = Config.ForceSoftwareRendering.ToString(),
            ["AllowUntrustedCertificates"] = Config.AllowUntrustedCertificates.ToString(),
            ["Theme"]                      = Config.Theme.ToString(),
            ["LogLevel"]                   = Config.LogLevel,
        });

        CleanupStaleUpdateArtifacts();

        // Generate a CA bundle from Windows' trust store and inject it into yt-dlp.
        // This is THE fix for managed environments with SSL inspection: yt-dlp's bundled
        // Python certifi doesn't know about the site-installed CA installed via GPO into
        // Windows' root store. Without this, yt-dlp's HTTPS handshake fails silently
        // and the metadata fetch hangs until our 30s timeout while the browser on the
        // same machine works fine (because browsers trust the Windows store directly).
        //
        // v1.1.17: switched from Environment.SetEnvironmentVariable + ProcessSandbox
        // pass-through to EXPLICIT INJECTION via YtDlpRunner constructor → built into
        // every ProcessStartArguments.ExtraEnv. The global-env round-trip was unreliable
        // on managed Windows hosts in v1.1.16 — even though the env var was set in
        // the parent process and the pass-through whitelist included SSL_CERT_FILE,
        // yt-dlp children launched via ProcessStartInfo did not see it. Explicit
        // injection on each ProcessStartArguments removes the global-env hop entirely.
        var caBundlePath = Path.Combine(Paths.DataRoot, "system-ca-bundle.pem");
        string? injectableCaBundle = null;
        if (SystemCertBundle.GenerateOrRefresh(caBundlePath, Logger))
        {
            injectableCaBundle = caBundlePath;
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

        // v1.2.4: write a permissive OpenSSL config so SECLEVEL=0 can be turned on for
        // yt-dlp child processes when the user opted into AllowUntrustedCertificates.
        // The file is created unconditionally (cheap, ~200 bytes) but OPENSSL_CONF is
        // only set in the env when the flag is on (see YtDlpRunner.BuildExtraEnv).
        var opensslConfPath = Path.Combine(Paths.DataRoot, "openssl-permissive.cnf");
        string? injectableOpensslConf = null;
        if (SystemCertBundle.WritePermissiveOpensslConf(opensslConfPath))
        {
            injectableOpensslConf = opensslConfPath;
            Logger.Info("openssl.conf.written", new Dictionary<string, string>
            {
                ["path"] = opensslConfPath,
            });
        }
        else
        {
            Logger.Warn("openssl.conf.write_failed", new Dictionary<string, string>
            {
                ["path"] = opensslConfPath,
            });
        }

        StateJournal = new StateJournal(Paths.StateLog);

        var ytDlpExe  = Path.Combine(Paths.BinDirectory, "yt-dlp.exe");
        var ffmpegExe = Path.Combine(Paths.BinDirectory, "ffmpeg.exe");
        YtDlp  = new YtDlpRunner(
            ytDlpExe,
            allowUntrustedCerts: Config.AllowUntrustedCertificates,
            caBundlePath: injectableCaBundle,
            opensslConfPath: injectableOpensslConf,
            logger: Logger);
        Ffmpeg = new FfmpegRunner(ffmpegExe);

        Logger.Info("ytdlp.ca-bundle.inject", new Dictionary<string, string>
        {
            ["enabled"] = (injectableCaBundle is not null).ToString(),
            ["path"]    = injectableCaBundle ?? "(disabled)",
        });

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

    // v1.2.2: delete leftover *.old files on startup. UpdateApplier renames the live
    // YtDlpTool.exe to YtDlpTool.exe.old before dropping the new one in place. On
    // Windows you CAN rename a running exe (the file handle tracks the inode, not
    // the path) but you CANNOT delete it while it's still open. UpdateApplier's
    // post-apply File.Delete therefore always fails silently for the running exe,
    // and the .old file was left around forever. Now the NEW process — launched
    // after the user restarts — does the cleanup, because by that point the old
    // process has released its handle. Best-effort: a Defender quarantine or AV
    // scanning the .old file briefly could still block deletion; in that case the
    // next launch tries again.
    private void CleanupStaleUpdateArtifacts()
    {
        try
        {
            var dirs = new[] { Paths.AppDirectory, Paths.BinDirectory };
            var deleted = 0;
            var failed = 0;
            foreach (var dir in dirs)
            {
                if (!Directory.Exists(dir)) continue;
                foreach (var oldFile in Directory.EnumerateFiles(dir, "*.old", SearchOption.TopDirectoryOnly))
                {
                    try
                    {
                        File.Delete(oldFile);
                        deleted++;
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        Logger.Warn("update.cleanup.stale_failed", new Dictionary<string, string>
                        {
                            ["path"]  = oldFile,
                            ["error"] = ex.Message,
                        });
                    }
                }
            }
            if (deleted > 0 || failed > 0)
            {
                Logger.Info("update.cleanup.stale", new Dictionary<string, string>
                {
                    ["deleted"] = deleted.ToString(),
                    ["failed"]  = failed.ToString(),
                });
            }
        }
        catch { /* best-effort, never throw at startup */ }
    }

    /// <summary>
    /// Probes installed yt-dlp and ffmpeg versions plus this app's version. Used by both the
    /// background update check and the Settings dialog's manual "check now" / "redownload components"
    /// flows so they share the exact same probe path.
    /// </summary>
    public async Task<InstalledVersions> GetInstalledVersionsAsync()
    {
        var app    = ThisVersion();
        var ytDlp  = await ProbeYtDlpVersionAsync().ConfigureAwait(false);
        var ffmpeg = await ProbeFfmpegVersionAsync().ConfigureAwait(false);
        // v1.3.2: log what each probe returned. Diagnoses persistent
        // "update available" prompts where the installed component's --version
        // output doesn't match the manifest's expected string (because the
        // probe timed out, the binary is from an older release, or the build
        // pipeline embedded a different version string).
        Logger.Info("version.probe", new Dictionary<string, string>
        {
            ["app"]    = app,
            ["ytdlp"]  = string.IsNullOrWhiteSpace(ytDlp)  ? "(probe-failed)" : ytDlp,
            ["ffmpeg"] = string.IsNullOrWhiteSpace(ffmpeg) ? "(probe-failed)" : ffmpeg,
        });
        return new(App: app, YtDlp: ytDlp, Ffmpeg: ffmpeg);
    }

    public UpdateBannerViewModel BannerVm { get; } = new();

    public async Task StartBackgroundUpdateCheckAsync(CancellationToken ct)
    {
        // Wait 5 seconds after startup before first check. Was 60s in v1.0–v1.2.1
        // when the default cadence was Weekly and the long delay made sense. v1.2.2
        // bumped AppCheckFrequency default to EveryLaunch, so a 60s delay would mean
        // short sessions never even see the banner. 5s is enough for the WPF first
        // frame + theme apply to settle without competing for network/CPU.
        try { await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false); }
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
        // v1.3.2: bumped timeout 5s → 30s. PyInstaller-frozen yt-dlp.exe self-
        // extracts to %TEMP% on first run and takes longer than 5s under
        // anti-virus scanning, which made --version time out and return empty
        // — UpdateChecker then read empty < manifest version, flagged yt-dlp
        // as "newer available" forever, and the user got an update prompt that
        // never went away. 30s is enough for a cold PyInstaller start on a
        // slow disk + AV scan.
        try
        {
            var args = new ProcessStartArguments(
                ExecutablePath: Path.Combine(Paths.BinDirectory, "yt-dlp.exe"),
                Arguments: new[] { "--version" },
                Timeout: TimeSpan.FromSeconds(30),
                StdoutByteLimit: 64 * 1024);
            var output = new System.Text.StringBuilder();
            var exit = await ProcessSandbox.RunAsync(args, l => { lock (output) output.AppendLine(l.Text); }).ConfigureAwait(false);
            return exit.ExitCode == 0 ? output.ToString().Trim() : "";
        }
        catch { return ""; }
    }

    private async Task<string> ProbeFfmpegVersionAsync()
    {
        // v1.3.2: 5s → 30s (same rationale as ProbeYtDlpVersionAsync above).
        try
        {
            var args = new ProcessStartArguments(
                ExecutablePath: Path.Combine(Paths.BinDirectory, "ffmpeg.exe"),
                Arguments: new[] { "-version" },
                Timeout: TimeSpan.FromSeconds(30),
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

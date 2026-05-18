namespace YtDlpTool.Domain.Models;

public sealed class AppConfig
{
    public int ConcurrentDownloads { get; set; } = 2;
    public string DefaultSaveDirectory { get; set; } = "";
    // v1.2.2: app + component check frequencies bumped Weekly → EveryLaunch so users
    // see new releases the first time they open the app. The check still happens in
    // the background ~5s after launch (StartBackgroundUpdateCheckAsync) so UI isn't
    // blocked, and the manifest is Sigstore-verified before any banner appears, so
    // there's no security cost to checking more often. Existing installs with
    // config.json keep whatever was previously stored — only new installs get this
    // default. Users on the old default who want every-launch can change it in
    // Settings → 更新.
    public UpdateCheckFrequency YtDlpCheckFrequency { get; set; } = UpdateCheckFrequency.EveryLaunch;
    public UpdateCheckFrequency FfmpegCheckFrequency { get; set; } = UpdateCheckFrequency.EveryLaunch;
    public UpdateCheckFrequency AppCheckFrequency { get; set; } = UpdateCheckFrequency.EveryLaunch;
    public ThemePreference Theme { get; set; } = ThemePreference.System;
    public string LanguageCode { get; set; } = "zh-TW";
    public DateTimeOffset? LastYtDlpCheck { get; set; }
    public DateTimeOffset? LastFfmpegCheck { get; set; }
    public DateTimeOffset? LastAppCheck { get; set; }
    public string LogLevel { get; set; } = "Info";

    // White-UI workaround for the rare machine where WPF hardware rendering produces
    // a blank/white visual tree (older Intel iGPU drivers, RDP sessions, certain AD
    // GPO configs). When true, App.OnStartup sets RenderOptions.ProcessRenderMode to
    // SoftwareOnly. Default false — leave hardware acceleration on for the majority.
    // To toggle: stop the app, edit config.json, set "ForceSoftwareRendering": true,
    // relaunch.
    public bool ForceSoftwareRendering { get; set; } = false;

    // Fallback for networks whose HTTPS inspection produces leaf certificates with
    // weak keys (< 2048-bit RSA), which OpenSSL's default SECLEVEL=1 rejects during
    // the TLS handshake before any Python-level validation runs.
    //
    // v1.2.4 behaviour: when true, yt-dlp's bundled OpenSSL receives an OPENSSL_CONF
    // env var pointing at a config that sets CipherString=DEFAULT@SECLEVEL=0. This
    // ALLOWS WEAK KEYS at the handshake layer, but CERT-CHAIN + HOSTNAME VALIDATION
    // STILL RUNS against the system trust store (corporate CA pulled in via
    // SystemCertBundle). So an attacker on an untrusted network can't MITM unless
    // they hold a cert signed by something the local trust store accepts.
    //
    // Pre-v1.2.4 behaviour was --no-check-certificates (full bypass — accepts ANY
    // cert from anyone). If reports show v1.2.4's SECLEVEL-only fallback
    // isn't enough (e.g., corporate CA not actually deployed to Windows root
    // store), we may add a second flag for the legacy full bypass.
    //
    // Default false. Should only be enabled on networks the user trusts.
    public bool AllowUntrustedCertificates { get; set; } = false;

    public static AppConfig CreateDefault()
    {
        return new AppConfig
        {
            // v1.1.31: default to <Desktop>\YtVideo per user feedback. New installs
            // get this. Existing installs keep whatever's in their config.json —
            // AppHost only applies the default when DefaultSaveDirectory is blank.
            // The folder itself is created just-in-time by YtDlpDownloadExecutor
            // before the first download, so we don't litter empty folders on the
            // desktop of users who never download anything.
            DefaultSaveDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                "YtVideo")
        };
    }
}

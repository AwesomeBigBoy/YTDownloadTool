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
    // v1.2.5 behaviour: when true, yt-dlp's bundled OpenSSL receives:
    //   1. OPENSSL_CONF env var pointing at openssl-permissive.cnf (best-effort —
    //      Python's ssl module ignores it on builds compiled with
    //      OPENSSL_INIT_NO_LOAD_CONFIG, which is the default since Python 3.8 —
    //      we still set it for OpenSSL builds that do honour it; harmless if not)
    //   2. --no-check-certificates CLI flag → verify_mode=CERT_NONE,
    //      check_hostname=False. Python ssl skips ALL cert validation including
    //      chain + hostname checks. Note: this STILL doesn't bypass OpenSSL's
    //      SECLEVEL handshake-time check on some Python builds, but works in
    //      practice for many endpoints whose proxy-generated cert is just barely
    //      acceptable.
    //
    // History:
    //   - v1.2.3 had only --no-check-certificates. Metadata worked, download
    //     failed on weak-key API hosts.
    //   - v1.2.4 tried OPENSSL_CONF only (no --no-check-certificates). Failed
    //     because Python doesn't honour OPENSSL_CONF.
    //   - v1.2.5 ships both → matches v1.2.3 minimum + retains the OPENSSL_CONF
    //     env var for future compatibility.
    //
    // SECURITY TRADEOFF: with --no-check-certificates enabled, yt-dlp accepts
    // ANY HTTPS certificate, regardless of issuer. On a hostile network (public
    // wifi, attacker-controlled router) this allows MITM. Should only be enabled
    // on networks the user trusts (corporate LAN with known SSL inspection).
    //
    // Default false.
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

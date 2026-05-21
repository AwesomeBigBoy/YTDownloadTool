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

    // Emergency fallback for managed environments where SystemCertBundle-based trust
    // doesn't fix SSL handshake (e.g., the SSL-inspection CA isn't in any Windows
    // root store, or IT installs it only in the user store of a non-current user).
    // When true, yt-dlp gets --no-check-certificates. SECURITY TRADEOFF: yt-dlp
    // will accept ANY HTTPS certificate without verification, so a hostile network
    // could MITM the YouTube traffic. Default false; document as IT-supported toggle.
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

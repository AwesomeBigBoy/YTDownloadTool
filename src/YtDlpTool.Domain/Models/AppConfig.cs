namespace YtDlpTool.Domain.Models;

public sealed class AppConfig
{
    public int ConcurrentDownloads { get; set; } = 2;
    public string DefaultSaveDirectory { get; set; } = "";
    public UpdateCheckFrequency YtDlpCheckFrequency { get; set; } = UpdateCheckFrequency.Weekly;
    public UpdateCheckFrequency FfmpegCheckFrequency { get; set; } = UpdateCheckFrequency.Weekly;
    public UpdateCheckFrequency AppCheckFrequency { get; set; } = UpdateCheckFrequency.Weekly;
    public ThemePreference Theme { get; set; } = ThemePreference.System;
    public string LanguageCode { get; set; } = "zh-TW";
    public DateTimeOffset? LastYtDlpCheck { get; set; }
    public DateTimeOffset? LastFfmpegCheck { get; set; }
    public DateTimeOffset? LastAppCheck { get; set; }
    public string LogLevel { get; set; } = "Info";

    public static AppConfig CreateDefault()
    {
        return new AppConfig
        {
            DefaultSaveDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Downloads", "YtDlpTool")
        };
    }
}

namespace YtDlpTool.Domain.Services;

/// <summary>
/// Whitelist of subtitle languages the app surfaces, mapping locale codes
/// (as emitted by yt-dlp) to zh-TW display names. Any subtitle whose
/// LanguageCode does not resolve here is dropped from the UI.
/// </summary>
public static class SupportedSubtitles
{
    private static readonly Dictionary<string, string> Languages = new(StringComparer.OrdinalIgnoreCase)
    {
        ["zh-TW"]      = "繁體中文",
        ["zh-Hant"]    = "繁體中文",
        ["zh-Hant-TW"] = "繁體中文",
        ["en"]         = "英文",
        ["en-US"]      = "英文",
        ["en-GB"]      = "英文",
        ["ja"]         = "日文",
        ["ja-JP"]      = "日文",
        ["vi"]         = "越南文",
        ["vi-VN"]      = "越南文",
        ["th"]         = "泰文",
        ["th-TH"]      = "泰文",
        ["id"]         = "印尼文",
        ["id-ID"]      = "印尼文",
        ["fil"]        = "菲律賓文",
        ["tl"]         = "菲律賓文",
        ["ko"]         = "韓文",
        ["ko-KR"]      = "韓文",
        ["hi"]         = "印度文",
        ["hi-IN"]      = "印度文",
    };

    public static string? GetDisplayName(string languageCode) =>
        Languages.TryGetValue(languageCode, out var name) ? name : null;
}

using System.Text;

namespace YtDlpTool.Domain.Services;

public static class FileNameSanitizer
{
    private const int MaxLength = 200;

    private static readonly HashSet<char> Forbidden = new("<>:\"/\\|?*".ToCharArray());

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON","PRN","AUX","NUL",
        "COM1","COM2","COM3","COM4","COM5","COM6","COM7","COM8","COM9",
        "LPT1","LPT2","LPT3","LPT4","LPT5","LPT6","LPT7","LPT8","LPT9"
    };

    /// <summary>
    /// v1.5.0: the file name for a download — the title, then the quality in brackets:
    /// 「Me at the zoo [1080p]」, 「… [129kbps]」. A long title is shortened so the whole
    /// name stays within the limit and the quality is always there. A title is not a file
    /// name, so a dot in it is not an extension here.
    /// </summary>
    public static string WithLabel(string? title, string label)
    {
        if (string.IsNullOrWhiteSpace(label)) return Sanitize(title);
        var suffix = " [" + label + "]";
        var name = Clean(title) ?? Fallback();
        var room = MaxLength - suffix.Length;
        if (name.Length > room) name = name[..room].TrimEnd(' ', '.');
        if (name.Length == 0) name = Fallback();
        return name + suffix;
    }

    private static string Fallback() => $"video_{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";

    // Forbidden and control characters out, no dot or space at the end; null when nothing is left.
    private static string? Clean(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var sb = new StringBuilder(raw.Length);
        foreach (var c in raw)
        {
            if (Forbidden.Contains(c)) { sb.Append('_'); continue; }
            if (char.IsControl(c)) continue;
            if (c == '\u202E' || c == '\u202D' || c == '\u200E' || c == '\u200F') continue;
            sb.Append(c);
        }
        var cleaned = sb.ToString().TrimEnd(' ', '.').TrimStart();
        return cleaned.Length == 0 ? null : cleaned;
    }

    public static string Sanitize(string? raw)
    {
        var cleaned = Clean(raw);
        if (cleaned is null) return Fallback();

        var dot = cleaned.LastIndexOf('.');
        var stem = dot > 0 ? cleaned[..dot] : cleaned;
        var ext  = dot > 0 ? cleaned[dot..] : "";

        if (ReservedNames.Contains(stem))
            cleaned = "_" + cleaned;

        if (cleaned.Length > MaxLength)
        {
            var room = MaxLength - ext.Length;
            cleaned = stem[..Math.Max(1, room)] + ext;
        }

        return cleaned;
    }
}

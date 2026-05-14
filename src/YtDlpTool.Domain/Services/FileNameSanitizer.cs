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

    public static string Sanitize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return $"video_{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";

        var sb = new StringBuilder(raw.Length);
        foreach (var c in raw)
        {
            if (Forbidden.Contains(c)) { sb.Append('_'); continue; }
            if (char.IsControl(c)) continue;
            if (c == '‮' || c == '‭' || c == '‎' || c == '‏') continue;
            sb.Append(c);
        }

        var cleaned = sb.ToString().TrimEnd(' ', '.');

        cleaned = cleaned.TrimStart();
        if (cleaned.Length == 0) return $"video_{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";

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

using System.Buffers.Binary;
using System.Collections.Concurrent;

namespace YtDlpTool.Process;

/// <summary>
/// v1.5.0: whether a file is a Windows program before it is started. A damaged yt-dlp.exe
/// that still begins with a DOS "MZ" header made Windows itself pop an "Unsupported 16-bit
/// application" box on every launch attempt; any other damage came back as a bare
/// "Win32 錯誤 216". Checked once per file version (path, size, last write).
/// </summary>
internal static class PeImage
{
    public const string DamagedHint = "元件檔案已損毀（不是有效的 Windows 程式）";

    private static readonly ConcurrentDictionary<string, (DateTime Written, long Length, bool Ok)> Checked =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// An MZ header whose e_lfanew points at a "PE\0\0" signature. A missing or unreadable
    /// file passes: Process.Start reports those as before (not found, access denied).
    /// </summary>
    public static bool LooksLikeAWindowsProgram(string path)
    {
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists) return true;
            if (Checked.TryGetValue(path, out var seen) && seen.Written == file.LastWriteTimeUtc && seen.Length == file.Length)
                return seen.Ok;
            var ok = HasPeSignature(path);
            Checked[path] = (file.LastWriteTimeUtc, file.Length, ok);
            return ok;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static bool HasPeSignature(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        Span<byte> dos = stackalloc byte[64];
        if (stream.ReadAtLeast(dos, dos.Length, throwOnEndOfStream: false) < dos.Length) return false;
        if (dos[0] != (byte)'M' || dos[1] != (byte)'Z') return false;
        var peOffset = BinaryPrimitives.ReadInt32LittleEndian(dos[0x3C..]);
        if (peOffset < dos.Length || peOffset > stream.Length - 4) return false;
        stream.Position = peOffset;
        Span<byte> signature = stackalloc byte[4];
        return stream.ReadAtLeast(signature, 4, throwOnEndOfStream: false) == 4 &&
               signature[0] == (byte)'P' && signature[1] == (byte)'E' && signature[2] == 0 && signature[3] == 0;
    }
}

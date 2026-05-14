using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace YtDlpTool.Domain.Logging;

public sealed class AppLogger : IDisposable
{
    private readonly string _logsDir;
    private readonly LogLevel _minLevel;
    private readonly Func<DateTime> _clock;
    private readonly object _gate = new();
    private StreamWriter? _writer;
    private string? _currentFile;

    public AppLogger(string logsDir, LogLevel minLevel, Func<DateTime> clock)
    {
        _logsDir = logsDir;
        _minLevel = minLevel;
        _clock = clock;
        Directory.CreateDirectory(logsDir);
    }

    public void Debug(string category, IReadOnlyDictionary<string, string>? fields = null) =>
        Write(LogLevel.Debug, category, fields);
    public void Info(string category, IReadOnlyDictionary<string, string>? fields = null) =>
        Write(LogLevel.Info, category, fields);
    public void Warn(string category, IReadOnlyDictionary<string, string>? fields = null) =>
        Write(LogLevel.Warn, category, fields);
    public void Error(string category, IReadOnlyDictionary<string, string>? fields = null) =>
        Write(LogLevel.Error, category, fields);

    private void Write(LogLevel level, string category, IReadOnlyDictionary<string, string>? fields)
    {
        if (level < _minLevel) return;
        var now = _clock();
        var fileForDay = Path.Combine(_logsDir, now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".log");

        lock (_gate)
        {
            if (_currentFile != fileForDay)
            {
                _writer?.Dispose();
                _writer = new StreamWriter(File.Open(fileForDay, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                {
                    AutoFlush = false
                };
                _currentFile = fileForDay;
            }
            _writer!.Write(now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture));
            _writer.Write(' '); _writer.Write(LevelLabel(level));
            _writer.Write(' '); _writer.Write(category);
            if (fields is not null)
            {
                foreach (var kv in fields)
                {
                    _writer.Write(' '); _writer.Write(kv.Key);
                    _writer.Write('='); _writer.Write(EscapeValue(kv.Value));
                }
            }
            _writer.WriteLine();
        }
    }

    public void Flush()
    {
        lock (_gate)
        {
            _writer?.Flush();
            // Release the file handle so external readers (and tests) can read
            // the file even on Windows where FileShare.ReadWrite isn't enough
            // for File.ReadAllText callers using default FileShare.Read.
            _writer?.Dispose();
            _writer = null;
            _currentFile = null;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _writer?.Flush();
            _writer?.Dispose();
            _writer = null;
        }
    }

    private static string LevelLabel(LogLevel l) => l switch
    {
        LogLevel.Debug => "DEBUG",
        LogLevel.Info  => "INFO ",
        LogLevel.Warn  => "WARN ",
        LogLevel.Error => "ERROR",
        _ => "?    "
    };

    private static string EscapeValue(string v) =>
        v.IndexOfAny(new[] { ' ', '\t', '"', '\n', '\r' }) < 0
            ? v
            : "\"" + v.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    public static string HashSuffix(string secret)
    {
        var bytes = Encoding.UTF8.GetBytes(secret);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash, 0, 4).ToLowerInvariant();
    }

    public static void PurgeOlderThan(string logsDir, TimeSpan maxAge, DateTime now)
    {
        if (!Directory.Exists(logsDir)) return;
        var cutoff = now - maxAge;
        var nameRegex = new Regex(@"^(\d{4}-\d{2}-\d{2})\.log$");
        foreach (var file in Directory.EnumerateFiles(logsDir, "*.log"))
        {
            var name = Path.GetFileName(file);
            var m = nameRegex.Match(name);
            if (!m.Success) continue;
            if (DateTime.TryParseExact(m.Groups[1].Value, "yyyy-MM-dd",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) &&
                date < cutoff)
            {
                try { File.Delete(file); } catch { /* best-effort */ }
            }
        }
    }
}

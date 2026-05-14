namespace YtDlpTool.Domain.Logging;

public sealed record LogEntry(
    DateTime TimestampUtc,
    LogLevel Level,
    string Category,
    IReadOnlyDictionary<string, string>? Fields);

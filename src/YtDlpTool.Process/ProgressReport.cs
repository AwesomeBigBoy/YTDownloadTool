namespace YtDlpTool.Process;

public sealed record ProgressReport(double Percent, long? BytesPerSecond, TimeSpan? Eta);

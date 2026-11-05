namespace YtDlpTool.Process;

/// <param name="Processing">v1.5.0: the bytes are in and yt-dlp's post-processors (merge,
/// cover, conversion, clean-up) are running.</param>
public sealed record ProgressReport(double Percent, long? BytesPerSecond, TimeSpan? Eta, bool Processing = false);

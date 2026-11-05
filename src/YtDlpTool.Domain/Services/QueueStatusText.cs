using System.Globalization;
using YtDlpTool.Domain.Models;

namespace YtDlpTool.Domain.Services;

/// <summary>
/// The one-line status under each queue row (v1.5.0). Pure, so the exact wording users
/// see is pinned by tests rather than by a XAML template.
/// </summary>
public static class QueueStatusText
{
    public static string Format(JobStatus status, double percent, long? bytesPerSecond, TimeSpan? eta, string? failureReason,
        bool processing = false, bool cancelling = false)
    {
        // v1.5.0: asked to stop; yt-dlp is gone at once, clearing its files can take a while.
        if (cancelling && status is JobStatus.Pending or JobStatus.Downloading) return "正在取消…";

        switch (status)
        {
            case JobStatus.Pending:   return "等待中";
            case JobStatus.Completed: return "已完成";
            case JobStatus.Cancelled: return "已取消";
            case JobStatus.Failed:    return string.IsNullOrWhiteSpace(failureReason) ? "下載失敗" : failureReason;
        }

        // Floored: "100%" must not appear while the last bytes or the merge still run.
        var shown = (int)Math.Floor(Math.Clamp(percent, 0, 100));
        var text = shown.ToString(CultureInfo.InvariantCulture) + "%";
        // v1.5.0: merging, the cover, converting, cleaning up — no speed to show, and on a
        // managed PC it can take longer than the download itself.
        if (processing) return text + " · 處理中…";
        if (bytesPerSecond is { } bps) text += " · " + FormatSpeed(bps);
        // yt-dlp reports 00:00 mid-download for fragmented streams; zero says nothing.
        if (eta is { } t && t > TimeSpan.Zero) text += " · 剩餘 " + FormatEta(t);
        return text;
    }

    public static string FormatSpeed(long bytesPerSecond)
    {
        double v = bytesPerSecond;
        var units = new[] { "B/s", "KB/s", "MB/s", "GB/s" };
        var u = 0;
        while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
        return v.ToString("0.#", CultureInfo.InvariantCulture) + " " + units[u];
    }

    private static string FormatEta(TimeSpan t) =>
        t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
            : $"{t.Minutes}:{t.Seconds:00}";
}

namespace YtDlpTool.Domain.Services;

/// <summary>v1.5.0: the status line under the URL box while and after a batch is added.</summary>
public static class BatchStatusText
{
    public static string Format(BatchProgress p)
    {
        if (p.IsRunning)
            return $"正在加入第 {Math.Min(p.Done + 1, Math.Max(p.Total, 1))} / {p.Total} 部影片…";

        if (p.AbortedOn is { } aborted)
        {
            var prefix = p.Added > 0 ? $"已加入 {p.Added} 部影片；" : "";
            return $"{prefix}連續 {BatchQueue.DefaultStopAfter} 部無法加入，已停止加入其餘 {p.Total - p.Done} 部：{aborted.UserMessage}";
        }

        var text = $"已加入 {p.Added} 部影片";
        if (p.Failed > 0) text += $"，{p.Failed} 部無法加入：{p.CommonError?.UserMessage ?? "原因不明"}";
        if (p.Skipped > 0) text += $"，略過 {p.Skipped} 部重複或已在下載清單中的影片";
        if (p.Stopped && p.Done < p.Total) text += $"，其餘 {p.Total - p.Done} 部已停止加入";
        return text;
    }
}

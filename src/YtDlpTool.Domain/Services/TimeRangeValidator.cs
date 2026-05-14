using System.Text.RegularExpressions;
using YtDlpTool.Domain.Models;

namespace YtDlpTool.Domain.Services;

public static class TimeRangeValidator
{
    private static readonly Regex Pattern =
        new(@"^([0-1]?\d|2[0-3]):[0-5]\d:[0-5]\d$", RegexOptions.Compiled);

    private static readonly TimeSpan MaxClipLength = TimeSpan.FromHours(8);

    public sealed record Result(bool IsValid, TimeRange? Range, string? Reason)
    {
        public static Result Ok(TimeRange r) => new(true, r, null);
        public static Result Fail(string reason) => new(false, null, reason);
    }

    public static Result Parse(string startText, string endText, TimeSpan videoDuration)
    {
        if (!Pattern.IsMatch(startText))
            return Result.Fail("開始時間格式錯誤（請用 hh:mm:ss）");
        if (!Pattern.IsMatch(endText))
            return Result.Fail("結束時間格式錯誤（請用 hh:mm:ss）");

        var start = TimeSpan.Parse(startText);
        var end   = TimeSpan.Parse(endText);

        if (end <= start)
            return Result.Fail("結束時間必須晚於開始時間");

        if (end > videoDuration)
            return Result.Fail($"結束時間超過影片長度（{videoDuration:hh\\:mm\\:ss}）");

        if ((end - start) > MaxClipLength)
            return Result.Fail("擷取片段長度不可超過 8 小時");

        return Result.Ok(new TimeRange(start, end));
    }
}

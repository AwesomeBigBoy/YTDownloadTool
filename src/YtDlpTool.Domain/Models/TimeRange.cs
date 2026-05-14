namespace YtDlpTool.Domain.Models;

public sealed record TimeRange(TimeSpan Start, TimeSpan End)
{
    public TimeSpan Duration => End - Start;

    public string ToYtDlpFormat() =>
        $"*{Start:hh\\:mm\\:ss}-{End:hh\\:mm\\:ss}";
}

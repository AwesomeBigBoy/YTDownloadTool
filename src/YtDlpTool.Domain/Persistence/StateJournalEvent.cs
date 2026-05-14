namespace YtDlpTool.Domain.Persistence;

public sealed class StateJournalEvent
{
    public DateTimeOffset At { get; set; }
    public StateJournalEventType Type { get; set; }
    public Guid JobId { get; set; }
    public JobSnapshot? Snapshot { get; set; }
    public double? ProgressPercent { get; set; }
    public string? FailureCode { get; set; }
}

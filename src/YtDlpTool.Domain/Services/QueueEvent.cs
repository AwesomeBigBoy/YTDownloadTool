using YtDlpTool.Domain.Models;

namespace YtDlpTool.Domain.Services;

public abstract record QueueEvent(DownloadJob Job);
public sealed record JobEnqueuedEvent(DownloadJob Job) : QueueEvent(Job);
public sealed record JobStartedEvent(DownloadJob Job) : QueueEvent(Job);
public sealed record JobProgressEvent(DownloadJob Job, DownloadProgressSnapshot Progress) : QueueEvent(Job);
public sealed record JobCompletedEvent(DownloadJob Job, string OutputFilePath) : QueueEvent(Job);
public sealed record JobFailedEvent(DownloadJob Job, MappedError Error) : QueueEvent(Job);
public sealed record JobCancelledEvent(DownloadJob Job) : QueueEvent(Job);

using YtDlpTool.Domain.Models;

namespace YtDlpTool.Process;

public sealed record MetadataFetchResult(
    bool IsSuccess,
    VideoMetadata? Metadata,
    string? ErrorStderr);

namespace YtDlpTool.Domain.Updates;

public sealed record UpdateAvailability(
    bool HasUpdate,
    UpdateManifest? Manifest,
    IReadOnlyList<ManifestFileEntry> NewerFiles,
    string? FailureReason);

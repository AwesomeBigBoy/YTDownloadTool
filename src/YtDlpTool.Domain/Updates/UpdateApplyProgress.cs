namespace YtDlpTool.Domain.Updates;

public enum UpdateApplyStage { Downloading, VerifyingHash, VerifyingSignature, Applying, Done, RolledBack, Failed }

public sealed record UpdateApplyProgress(
    UpdateApplyStage Stage,
    string FileName,
    double FilePercent,
    int FileIndex,
    int FileCount);

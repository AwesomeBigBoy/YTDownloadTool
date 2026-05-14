namespace YtDlpTool.Domain.Security;

public sealed record SigstoreVerificationResult(bool IsValid, string? FailureReason)
{
    public static SigstoreVerificationResult Ok() => new(true, null);
    public static SigstoreVerificationResult Fail(string reason) => new(false, reason);
}

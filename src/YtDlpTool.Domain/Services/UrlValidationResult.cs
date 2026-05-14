namespace YtDlpTool.Domain.Services;

public sealed record UrlValidationResult(bool IsValid, string? CanonicalUrl, string? Reason)
{
    public static UrlValidationResult Ok(string canonical) => new(true, canonical, null);
    public static UrlValidationResult Fail(string reason) => new(false, null, reason);
}

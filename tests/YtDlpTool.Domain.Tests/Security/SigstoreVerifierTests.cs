using YtDlpTool.Domain.Security;

namespace YtDlpTool.Domain.Tests.Security;

public class SigstoreVerifierTests
{
    private static readonly SigstoreVerifierOptions DefaultOptions = new(
        ExpectedIssuer: "https://token.actions.githubusercontent.com",
        ExpectedSanRegex: @"^https://github\.com/owner/repo/\.github/workflows/release\.yml@refs/tags/v.*$",
        TrustedRootPem: SigstoreRoots.FulcioRootPem
    );

    [Fact]
    public void Verify_NullBundle_Fails()
    {
        var result = SigstoreVerifier.Verify(
            artifactBytes: new byte[] { 1, 2, 3 },
            bundleJson: "",
            options: DefaultOptions);
        Assert.False(result.IsValid);
    }

    [Fact]
    public void Verify_MalformedBundle_Fails()
    {
        var result = SigstoreVerifier.Verify(
            artifactBytes: new byte[] { 1, 2, 3 },
            bundleJson: "{ not valid }",
            options: DefaultOptions);
        Assert.False(result.IsValid);
        Assert.Contains("解析", result.FailureReason!);
    }

    [Fact]
    public void Verify_BundleMissingCert_Fails()
    {
        const string json = """{"messageSignature":{"signature":"abc","messageDigest":{"algorithm":"SHA2_256","digest":"00"}}}""";
        var result = SigstoreVerifier.Verify(
            artifactBytes: new byte[] { 0 },
            bundleJson: json,
            options: DefaultOptions);
        Assert.False(result.IsValid);
    }

    // Note: full pass-case tests require a real bundle from the first CI run.
    // Those land in Phase 10 with a regression fixture under tests/fixtures/sigstore/.
}

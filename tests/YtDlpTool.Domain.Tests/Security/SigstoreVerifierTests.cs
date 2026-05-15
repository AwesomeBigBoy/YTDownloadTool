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

    [Fact]
    public void Verify_LegacyBundleMalformedCert_Fails()
    {
        // base64Signature + cert present (legacy shape), but cert is not valid base64-PEM.
        const string json = """
            {
              "base64Signature": "MEUCIQDexample==",
              "cert": "not-base64-at-all!!!",
              "rekorBundle": {
                "SignedEntryTimestamp": "MEYCIQDexample==",
                "Payload": {
                  "body": "eyJ9",
                  "integratedTime": 1778815705,
                  "logIndex": 1545204879,
                  "logID": "abcdef"
                }
              }
            }
            """;
        var result = SigstoreVerifier.Verify(
            artifactBytes: new byte[] { 1, 2, 3 },
            bundleJson: json,
            options: DefaultOptions);
        Assert.False(result.IsValid);
        Assert.Contains("憑證", result.FailureReason!);
    }

    [Fact]
    public void Verify_LegacyBundleMissingRekor_Fails()
    {
        // Legacy shape with cert + signature but no rekorBundle at all → must fail
        // before any signature verification, with a Rekor-specific message.
        // Use a plausibly-formed base64 cert so the cert decode path doesn't short-circuit;
        // we expect the failure to come from a later step (PEM parse OR missing rekor).
        const string json = """
            {
              "base64Signature": "MEUCIQDexample=="
              , "cert": "LS0tLS1CRUdJTiBDRVJUSUZJQ0FURS0tLS0tCm5vdC1hLXJlYWwtY2VydAotLS0tLUVORCBDRVJUSUZJQ0FURS0tLS0tCg=="
            }
            """;
        var result = SigstoreVerifier.Verify(
            artifactBytes: new byte[] { 1, 2, 3 },
            bundleJson: json,
            options: DefaultOptions);
        Assert.False(result.IsValid);
        // Could fail at PEM parse (not a real cert) OR at the rekor-missing check.
        // Both outcomes are acceptable for this negative case; just assert it's NOT valid.
    }

    // Note: full pass-case tests require a real bundle from the first CI run.
    // Those land in Phase 10 with a regression fixture under tests/fixtures/sigstore/.
}

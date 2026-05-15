using System.Text.Json;
using YtDlpTool.Domain.Security;

namespace YtDlpTool.Domain.Tests.Security;

public class SigstoreBundleTests
{
    [Fact]
    public void Parse_MinimalBundle_Succeeds()
    {
        const string json = """
            {
              "mediaType": "application/vnd.dev.sigstore.bundle+json;version=0.2",
              "verificationMaterial": {
                "certificate": { "rawBytes": "MIIDxxxxx" },
                "tlogEntries": [{
                  "logIndex": "12345",
                  "integratedTime": "1715600000",
                  "logId": { "keyId": "abc" },
                  "kindVersion": "rekord 0.0.1",
                  "inclusionPromise": { "signedEntryTimestamp": "MEUCxxxx" }
                }]
              },
              "messageSignature": {
                "messageDigest": { "algorithm": "SHA2_256", "digest": "deadbeef" },
                "signature": "MEYCxxxx"
              }
            }
            """;

        var bundle = JsonSerializer.Deserialize(json, SigstoreJsonContext.Default.SigstoreBundle);
        Assert.NotNull(bundle);
        Assert.NotNull(bundle!.VerificationMaterial);
        Assert.Equal("MIIDxxxxx", bundle.VerificationMaterial!.Certificate.RawBytes);
        Assert.Single(bundle.VerificationMaterial.TlogEntries);
        Assert.Equal("12345", bundle.VerificationMaterial.TlogEntries[0].LogIndex);
        Assert.Equal("SHA2_256", bundle.MessageSignature.MessageDigest.Algorithm);
    }

    [Fact]
    public void Parse_X509CertificateChainShape_Succeeds()
    {
        // Cosign sign-blob (>= v2) emits this shape, not verificationMaterial.certificate.
        const string json = """
            {
              "mediaType": "application/vnd.dev.sigstore.bundle+json;version=0.2",
              "verificationMaterial": {
                "x509CertificateChain": {
                  "certificates": [
                    { "rawBytes": "MIIDleaf" },
                    { "rawBytes": "MIIDintermediate" }
                  ]
                },
                "tlogEntries": [{
                  "logIndex": "12345",
                  "integratedTime": "1715600000",
                  "logId": { "keyId": "abc" },
                  "kindVersion": "rekord 0.0.1",
                  "inclusionPromise": { "signedEntryTimestamp": "MEUCxxxx" }
                }]
              },
              "messageSignature": {
                "messageDigest": { "algorithm": "SHA2_256", "digest": "deadbeef" },
                "signature": "MEYCxxxx"
              }
            }
            """;

        var bundle = JsonSerializer.Deserialize(json, SigstoreJsonContext.Default.SigstoreBundle);
        Assert.NotNull(bundle);
        Assert.NotNull(bundle!.VerificationMaterial);
        Assert.Empty(bundle.VerificationMaterial!.Certificate.RawBytes);
        Assert.Equal(2, bundle.VerificationMaterial.X509CertificateChain.Certificates.Length);
        Assert.Equal("MIIDleaf", bundle.VerificationMaterial.X509CertificateChain.Certificates[0].RawBytes);
        Assert.Equal("MIIDintermediate", bundle.VerificationMaterial.X509CertificateChain.Certificates[1].RawBytes);
    }

    [Fact]
    public void Parse_LegacyFormat_PopulatesLegacyFields()
    {
        // Sample of the cosign sign-blob --bundle legacy wire format (the shape
        // emitted by release.yml in v1.0.4..v1.1.7). Top-level fields are camelCase
        // but the rekorBundle inner children use PascalCase for SignedEntryTimestamp
        // and Payload.
        const string json = """
            {
              "base64Signature": "MEUCIQDexampleSignatureBytes==",
              "cert": "LS0tLS1CRUdJTiBDRVJUSUZJQ0FURS0tLS0tCk1JSURleGFtcGxlCi0tLS0tRU5EIENFUlRJRklDQVRFLS0tLS0K",
              "rekorBundle": {
                "SignedEntryTimestamp": "MEYCIQDexampleSetBytes==",
                "Payload": {
                  "body": "eyJraW5kIjoiaGFzaGVkcmVrb3JkIiwiYXBpVmVyc2lvbiI6IjAuMC4xIn0=",
                  "integratedTime": 1778815705,
                  "logIndex": 1545204879,
                  "logID": "c0d23d6ad406973f9559f3ba2d1ca01f84147d8ffc5b8445c224f98b9591801d"
                }
              }
            }
            """;

        var bundle = JsonSerializer.Deserialize(json, SigstoreJsonContext.Default.SigstoreBundle);
        Assert.NotNull(bundle);
        Assert.Equal("MEUCIQDexampleSignatureBytes==", bundle!.Base64Signature);
        Assert.Equal(
            "LS0tLS1CRUdJTiBDRVJUSUZJQ0FURS0tLS0tCk1JSURleGFtcGxlCi0tLS0tRU5EIENFUlRJRklDQVRFLS0tLS0K",
            bundle.Cert);
        Assert.NotNull(bundle.RekorBundle);
        Assert.Equal("MEYCIQDexampleSetBytes==", bundle.RekorBundle!.SignedEntryTimestamp);
        Assert.NotNull(bundle.RekorBundle.Payload);
        Assert.Equal(1778815705L, bundle.RekorBundle.Payload!.IntegratedTime);
        Assert.Equal(1545204879L, bundle.RekorBundle.Payload.LogIndex);
        Assert.Equal("c0d23d6ad406973f9559f3ba2d1ca01f84147d8ffc5b8445c224f98b9591801d", bundle.RekorBundle.Payload.LogID);
        Assert.Equal("eyJraW5kIjoiaGFzaGVkcmVrb3JkIiwiYXBpVmVyc2lvbiI6IjAuMC4xIn0=", bundle.RekorBundle.Payload.Body);
        // Protobuf-format fields should NOT be populated for a legacy bundle.
        Assert.Null(bundle.VerificationMaterial);
    }
}

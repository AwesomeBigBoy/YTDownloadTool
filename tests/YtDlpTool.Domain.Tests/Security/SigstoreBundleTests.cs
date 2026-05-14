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
        Assert.Equal("MIIDxxxxx", bundle!.VerificationMaterial.Certificate.RawBytes);
        Assert.Single(bundle.VerificationMaterial.TlogEntries);
        Assert.Equal("12345", bundle.VerificationMaterial.TlogEntries[0].LogIndex);
        Assert.Equal("SHA2_256", bundle.MessageSignature.MessageDigest.Algorithm);
    }
}

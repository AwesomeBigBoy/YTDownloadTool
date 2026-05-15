using System.Text.Json.Serialization;

namespace YtDlpTool.Domain.Security;

public sealed class SigstoreBundle
{
    // Protobuf bundle format (produced by `cosign sign-blob --bundle` from cosign v2 with
    // `--new-bundle-format`, and by `cosign attest-blob`). VerificationMaterial is nullable
    // because legacy bundles do not populate it.
    public string? MediaType { get; set; }
    public SigstoreVerificationMaterial? VerificationMaterial { get; set; }
    public SigstoreMessageSignature MessageSignature { get; set; } = new();

    // Legacy cosign bundle format (default output of `cosign sign-blob --bundle`).
    // Top-level fields are camelCase but the rekorBundle inner object uses PascalCase
    // for `SignedEntryTimestamp` and `Payload`, so we tag those explicitly because our
    // JsonSourceGenerationOptions defaults to CamelCase.
    [JsonPropertyName("base64Signature")]
    public string? Base64Signature { get; set; }

    [JsonPropertyName("cert")]
    public string? Cert { get; set; }

    [JsonPropertyName("rekorBundle")]
    public LegacyRekorBundle? RekorBundle { get; set; }
}

public sealed class SigstoreVerificationMaterial
{
    public SigstoreCertChain Certificate { get; set; } = new();
    public SigstoreCertificateChain X509CertificateChain { get; set; } = new();
    public SigstoreTlogEntry[] TlogEntries { get; set; } = Array.Empty<SigstoreTlogEntry>();
}

public sealed class SigstoreCertChain
{
    public string RawBytes { get; set; } = "";
}

public sealed class SigstoreCertificateChain
{
    public SigstoreCertChain[] Certificates { get; set; } = Array.Empty<SigstoreCertChain>();
}

public sealed class SigstoreTlogEntry
{
    public string LogIndex { get; set; } = "";
    public string IntegratedTime { get; set; } = "";
    public SigstoreLogId LogId { get; set; } = new();
    public string KindVersion { get; set; } = "";
    public SigstoreInclusionPromise? InclusionPromise { get; set; }
    public SigstoreCanonicalizedBody? CanonicalizedBody { get; set; }
}

public sealed class SigstoreLogId { public string KeyId { get; set; } = ""; }
public sealed class SigstoreInclusionPromise { public string SignedEntryTimestamp { get; set; } = ""; }
public sealed class SigstoreCanonicalizedBody { public string Body { get; set; } = ""; }

public sealed class SigstoreMessageSignature
{
    public SigstoreMessageDigest MessageDigest { get; set; } = new();
    public string Signature { get; set; } = "";
}

public sealed class SigstoreMessageDigest
{
    public string Algorithm { get; set; } = "";
    public string Digest { get; set; } = "";
}

// Legacy cosign rekorBundle child object. Note the PascalCase property names – this is
// the historical wire format from cosign v1 / pre-protobuf-bundle cosign v2.
public sealed class LegacyRekorBundle
{
    [JsonPropertyName("SignedEntryTimestamp")]
    public string? SignedEntryTimestamp { get; set; }

    [JsonPropertyName("Payload")]
    public LegacyRekorPayload? Payload { get; set; }
}

public sealed class LegacyRekorPayload
{
    [JsonPropertyName("body")]
    public string? Body { get; set; }

    [JsonPropertyName("integratedTime")]
    public long IntegratedTime { get; set; }

    [JsonPropertyName("logIndex")]
    public long LogIndex { get; set; }

    [JsonPropertyName("logID")]
    public string? LogID { get; set; }
}

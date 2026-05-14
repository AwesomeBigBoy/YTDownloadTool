namespace YtDlpTool.Domain.Security;

public sealed class SigstoreBundle
{
    public string? MediaType { get; set; }
    public SigstoreVerificationMaterial VerificationMaterial { get; set; } = new();
    public SigstoreMessageSignature MessageSignature { get; set; } = new();
}

public sealed class SigstoreVerificationMaterial
{
    public SigstoreCertChain Certificate { get; set; } = new();
    public SigstoreTlogEntry[] TlogEntries { get; set; } = Array.Empty<SigstoreTlogEntry>();
}

public sealed class SigstoreCertChain
{
    public string RawBytes { get; set; } = "";
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

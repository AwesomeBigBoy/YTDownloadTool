namespace YtDlpTool.Domain.Security;

public static class SigstoreRoots
{
    // Current production Fulcio root certificate (PEM).
    // Source: https://github.com/sigstore/root-signing — update on Sigstore key rotation.
    // For Phase 3 we use a placeholder; Phase 10 task 10.x replaces with the real PEM and
    // updates the Ed25519/SigstoreVerifier tests accordingly.
    public const string FulcioRootPem = "-----BEGIN CERTIFICATE-----\n<replaced-in-phase-10>\n-----END CERTIFICATE-----\n";

    // Rekor signing key (DER public key) — same caveat.
    public const string RekorPublicKeyPem = "-----BEGIN PUBLIC KEY-----\n<replaced-in-phase-10>\n-----END PUBLIC KEY-----\n";
}

using NSec.Cryptography;

namespace YtDlpTool.Domain.Security;

public static class Ed25519Verifier
{
    private static readonly SignatureAlgorithm Alg = SignatureAlgorithm.Ed25519;

    public static bool Verify(
        ReadOnlySpan<byte> message,
        ReadOnlySpan<byte> signature,
        ReadOnlySpan<byte> publicKey)
    {
        if (signature.Length != 64) return false;
        if (publicKey.Length != 32) return false;

        try
        {
            var key = PublicKey.Import(Alg, publicKey, KeyBlobFormat.RawPublicKey);
            return Alg.Verify(key, message, signature);
        }
        catch
        {
            return false;
        }
    }
}

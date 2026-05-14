using System.Security.Cryptography;

namespace YtDlpTool.Domain.Security;

public static class Sha256Verifier
{
    public static string ComputeHex(ReadOnlySpan<byte> data)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(data, hash);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static string ComputeFileHex(string path)
    {
        using var stream = File.OpenRead(path);
        var hash = SHA256.HashData(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static bool VerifyFile(string path, string expectedHex)
    {
        var actual = ComputeFileHex(path);
        return string.Equals(actual, expectedHex, StringComparison.OrdinalIgnoreCase);
    }
}

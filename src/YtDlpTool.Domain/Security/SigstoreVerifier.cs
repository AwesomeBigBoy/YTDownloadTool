using System.Formats.Asn1;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace YtDlpTool.Domain.Security;

public sealed record SigstoreVerifierOptions(
    string ExpectedIssuer,
    string ExpectedSanRegex,
    string TrustedRootPem);

public static class SigstoreVerifier
{
    public static SigstoreVerificationResult Verify(
        ReadOnlySpan<byte> artifactBytes,
        string bundleJson,
        SigstoreVerifierOptions options)
    {
        if (string.IsNullOrWhiteSpace(bundleJson))
            return SigstoreVerificationResult.Fail("簽章資料為空");

        SigstoreBundle? bundle;
        try
        {
            bundle = JsonSerializer.Deserialize(bundleJson, SigstoreJsonContext.Default.SigstoreBundle);
        }
        catch (JsonException ex)
        {
            return SigstoreVerificationResult.Fail($"解析簽章資料失敗：{ex.Message}");
        }

        if (bundle is null)
            return SigstoreVerificationResult.Fail("簽章資料無內容");

        // Cosign sign-blob emits verificationMaterial.x509CertificateChain.certificates[0].rawBytes,
        // while older / alternative producers may use verificationMaterial.certificate.rawBytes.
        // Accept both shapes.
        var certRaw = bundle.VerificationMaterial.Certificate.RawBytes;
        if (string.IsNullOrWhiteSpace(certRaw) && bundle.VerificationMaterial.X509CertificateChain.Certificates.Length > 0)
            certRaw = bundle.VerificationMaterial.X509CertificateChain.Certificates[0].RawBytes;
        if (string.IsNullOrWhiteSpace(certRaw))
            return SigstoreVerificationResult.Fail("簽章缺少憑證");

        X509Certificate2 leaf;
        try
        {
            var certDer = Convert.FromBase64String(certRaw);
            leaf = new X509Certificate2(certDer);
        }
        catch (Exception ex)
        {
            return SigstoreVerificationResult.Fail($"憑證解析失敗：{ex.Message}");
        }

        var sanCheck = ValidateSan(leaf, options.ExpectedSanRegex, options.ExpectedIssuer);
        if (!sanCheck.IsValid) return sanCheck;

        if (bundle.VerificationMaterial.TlogEntries.Length == 0)
            return SigstoreVerificationResult.Fail("缺少 Rekor 透明日誌條目");

        if (!long.TryParse(bundle.VerificationMaterial.TlogEntries[0].IntegratedTime,
            NumberStyles.Integer, CultureInfo.InvariantCulture, out var integratedUnix))
            return SigstoreVerificationResult.Fail("Rekor 時間戳格式錯誤");

        var integratedAt = DateTimeOffset.FromUnixTimeSeconds(integratedUnix);
        if (integratedAt < leaf.NotBefore || integratedAt > leaf.NotAfter)
            return SigstoreVerificationResult.Fail("簽章時間不在憑證有效期內");

        var chainCheck = ValidateChain(leaf, options.TrustedRootPem);
        if (!chainCheck.IsValid) return chainCheck;

        var sigCheck = ValidateSignature(leaf, artifactBytes, bundle.MessageSignature);
        if (!sigCheck.IsValid) return sigCheck;

        return SigstoreVerificationResult.Ok();
    }

    private static SigstoreVerificationResult ValidateSan(
        X509Certificate2 cert, string expectedSanRegex, string expectedIssuer)
    {
        var sanRegex = new Regex(expectedSanRegex, RegexOptions.Compiled);
        var sanExtension = cert.Extensions["2.5.29.17"];
        if (sanExtension is null)
            return SigstoreVerificationResult.Fail("憑證缺少 Subject Alternative Name");

        // Parse SubjectAltName ASN.1 directly (locale-independent, AOT-safe).
        // RFC 5280: SubjectAltName ::= GeneralNames ::= SEQUENCE OF GeneralName.
        // GeneralName ::= CHOICE { ... uniformResourceIdentifier [6] IA5String ... }
        var matched = false;
        try
        {
            var reader = new AsnReader(sanExtension.RawData, AsnEncodingRules.DER);
            var seq = reader.ReadSequence();
            while (seq.HasData)
            {
                var tag = seq.PeekTag();
                if (tag.TagClass == TagClass.ContextSpecific && tag.TagValue == 6)
                {
                    var uri = seq.ReadCharacterString(UniversalTagNumber.IA5String, tag);
                    if (sanRegex.IsMatch(uri)) { matched = true; break; }
                }
                else
                {
                    seq.ReadEncodedValue();
                }
            }
        }
        catch (AsnContentException ex)
        {
            return SigstoreVerificationResult.Fail($"SAN 解析失敗：{ex.Message}");
        }

        if (!matched)
            return SigstoreVerificationResult.Fail("憑證身份不符預期簽署者");

        // Fulcio embeds OIDC issuer as a custom extension OID 1.3.6.1.4.1.57264.1.1 (legacy raw UTF-8)
        // or 1.3.6.1.4.1.57264.1.8 (newer DER-encoded UTF8String). Try both.
        bool foundIssuer = false;
        foreach (var ext in cert.Extensions)
        {
            if (ext.Oid?.Value is "1.3.6.1.4.1.57264.1.1")
            {
                // Legacy: raw UTF-8 string in extension value.
                var raw = Encoding.UTF8.GetString(ext.RawData);
                if (raw.Contains(expectedIssuer, StringComparison.Ordinal)) { foundIssuer = true; break; }
            }
            else if (ext.Oid?.Value is "1.3.6.1.4.1.57264.1.8")
            {
                // Newer: DER-encoded UTF8String.
                try
                {
                    var r = new AsnReader(ext.RawData, AsnEncodingRules.DER);
                    var s = r.ReadCharacterString(UniversalTagNumber.UTF8String);
                    if (s.Contains(expectedIssuer, StringComparison.Ordinal)) { foundIssuer = true; break; }
                }
                catch (AsnContentException)
                {
                    // Fall back to raw bytes containing the string.
                    var raw = Encoding.UTF8.GetString(ext.RawData);
                    if (raw.Contains(expectedIssuer, StringComparison.Ordinal)) { foundIssuer = true; break; }
                }
            }
        }
        if (!foundIssuer)
            return SigstoreVerificationResult.Fail("憑證 OIDC issuer 不符預期");

        return SigstoreVerificationResult.Ok();
    }

    private static SigstoreVerificationResult ValidateChain(X509Certificate2 leaf, string trustedRootPem)
    {
        try
        {
            using var root = X509Certificate2.CreateFromPem(trustedRootPem);
            using var chain = new X509Chain();
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.Add(root);
            // Sigstore short-lived certs commonly trigger NotTimeValid on later verification;
            // we already validated NotBefore/NotAfter against the integrated time above.
            chain.ChainPolicy.VerificationFlags = X509VerificationFlags.IgnoreNotTimeValid;

            if (!chain.Build(leaf))
            {
                var reasons = string.Join(", ",
                    chain.ChainStatus.Select(s => s.StatusInformation.Trim()));
                return SigstoreVerificationResult.Fail($"憑證鏈無法建立：{reasons}");
            }
            return SigstoreVerificationResult.Ok();
        }
        catch (Exception ex)
        {
            return SigstoreVerificationResult.Fail($"憑證鏈驗證失敗：{ex.Message}");
        }
    }

    private static SigstoreVerificationResult ValidateSignature(
        X509Certificate2 leaf, ReadOnlySpan<byte> artifactBytes,
        SigstoreMessageSignature sigInfo)
    {
        if (!string.Equals(sigInfo.MessageDigest.Algorithm, "SHA2_256", StringComparison.OrdinalIgnoreCase))
            return SigstoreVerificationResult.Fail($"未支援的摘要演算法：{sigInfo.MessageDigest.Algorithm}");

        Span<byte> actualDigest = stackalloc byte[32];
        SHA256.HashData(artifactBytes, actualDigest);
        var actualHex = Convert.ToHexString(actualDigest);
        var expectedHex = NormalizeDigest(sigInfo.MessageDigest.Digest);
        if (!string.Equals(actualHex, expectedHex, StringComparison.OrdinalIgnoreCase))
            return SigstoreVerificationResult.Fail("檔案內容與簽章摘要不符");

        byte[] signature;
        try { signature = Convert.FromBase64String(sigInfo.Signature); }
        catch { return SigstoreVerificationResult.Fail("簽章編碼錯誤"); }

        bool ok;
        using (var ecdsa = leaf.GetECDsaPublicKey())
        {
            if (ecdsa is null) return SigstoreVerificationResult.Fail("憑證沒有可用的公鑰");
            ok = ecdsa.VerifyData(artifactBytes, signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        }
        return ok
            ? SigstoreVerificationResult.Ok()
            : SigstoreVerificationResult.Fail("簽章驗證失敗");
    }

    private static string NormalizeDigest(string maybeBase64OrHex)
    {
        if (Regex.IsMatch(maybeBase64OrHex, "^[0-9A-Fa-f]+$"))
            return maybeBase64OrHex;
        try
        {
            var bytes = Convert.FromBase64String(maybeBase64OrHex);
            return Convert.ToHexString(bytes);
        }
        catch
        {
            return maybeBase64OrHex;
        }
    }
}

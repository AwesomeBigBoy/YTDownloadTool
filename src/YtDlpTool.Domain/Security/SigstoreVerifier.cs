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

        // The release.yml workflow uses `cosign sign-blob --bundle` which (without the
        // newer --new-bundle-format flag) emits the legacy cosign bundle wire shape:
        //   { base64Signature, cert, rekorBundle: { SignedEntryTimestamp, Payload } }
        // The protobuf-bundle shape (verificationMaterial.{certificate|x509CertificateChain})
        // is only produced by cosign v2 + --new-bundle-format, or by `cosign attest-blob`.
        // Dispatch on whichever shape is populated.
        if (!string.IsNullOrEmpty(bundle.Cert) && !string.IsNullOrEmpty(bundle.Base64Signature))
            return VerifyLegacy(artifactBytes, bundle, options);
        if (bundle.VerificationMaterial is not null)
            return VerifyProtobuf(artifactBytes, bundle, options);

        return SigstoreVerificationResult.Fail("簽章格式無法辨識");
    }

    private static SigstoreVerificationResult VerifyProtobuf(
        ReadOnlySpan<byte> artifactBytes, SigstoreBundle bundle, SigstoreVerifierOptions options)
    {
        var material = bundle.VerificationMaterial!;

        // Cosign sign-blob emits verificationMaterial.x509CertificateChain.certificates[0].rawBytes,
        // while older / alternative producers may use verificationMaterial.certificate.rawBytes.
        // Accept both shapes.
        var certRaw = material.Certificate.RawBytes;
        if (string.IsNullOrWhiteSpace(certRaw) && material.X509CertificateChain.Certificates.Length > 0)
            certRaw = material.X509CertificateChain.Certificates[0].RawBytes;
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

        if (material.TlogEntries.Length == 0)
            return SigstoreVerificationResult.Fail("缺少 Rekor 透明日誌條目");

        if (!long.TryParse(material.TlogEntries[0].IntegratedTime,
            NumberStyles.Integer, CultureInfo.InvariantCulture, out var integratedUnix))
            return SigstoreVerificationResult.Fail("Rekor 時間戳格式錯誤");

        var integratedAt = DateTimeOffset.FromUnixTimeSeconds(integratedUnix);
        if (integratedAt < leaf.NotBefore || integratedAt > leaf.NotAfter)
            return SigstoreVerificationResult.Fail("簽章時間不在憑證有效期內");

        // Verify Rekor SignedEntryTimestamp — proves Rekor accepted this entry at
        // integratedTime; otherwise IntegratedTime is attacker-controlled.
        var setCheck = ValidateRekorSet(material.TlogEntries[0], integratedUnix);
        if (!setCheck.IsValid) return setCheck;

        var chainCheck = ValidateChain(leaf, options.TrustedRootPem);
        if (!chainCheck.IsValid) return chainCheck;

        var sigCheck = ValidateSignature(leaf, artifactBytes, bundle.MessageSignature);
        if (!sigCheck.IsValid) return sigCheck;

        return SigstoreVerificationResult.Ok();
    }

    private static SigstoreVerificationResult VerifyLegacy(
        ReadOnlySpan<byte> artifactBytes, SigstoreBundle bundle, SigstoreVerifierOptions options)
    {
        // (a) Decode the cert. In the legacy bundle, `cert` is base64-encoded PEM text
        // (so it's base64(PEM-string), where the PEM body is itself base64(DER)). Two-step
        // decode: outer base64 → PEM string, then X509Certificate2.CreateFromPem handles
        // the inner base64.
        string pemText;
        try
        {
            var pemBytes = Convert.FromBase64String(bundle.Cert!);
            pemText = Encoding.UTF8.GetString(pemBytes);
        }
        catch (Exception ex)
        {
            return SigstoreVerificationResult.Fail($"憑證 base64 解碼失敗：{ex.Message}");
        }

        X509Certificate2 leaf;
        try
        {
            leaf = X509Certificate2.CreateFromPem(pemText);
        }
        catch (Exception ex)
        {
            return SigstoreVerificationResult.Fail($"憑證 PEM 解析失敗：{ex.Message}");
        }

        // (b) SAN + OIDC issuer match.
        var sanCheck = ValidateSan(leaf, options.ExpectedSanRegex, options.ExpectedIssuer);
        if (!sanCheck.IsValid) return sanCheck;

        // (c) Time validity at integratedTime (from rekorBundle.Payload).
        if (bundle.RekorBundle?.Payload is null)
            return SigstoreVerificationResult.Fail("Rekor 資料缺失");

        var integratedUnix = bundle.RekorBundle.Payload.IntegratedTime;
        var integratedAt = DateTimeOffset.FromUnixTimeSeconds(integratedUnix);
        if (integratedAt < leaf.NotBefore || integratedAt > leaf.NotAfter)
            return SigstoreVerificationResult.Fail("簽章時間不在憑證有效期內");

        // (d) Cert chain to Fulcio root.
        var chainCheck = ValidateChain(leaf, options.TrustedRootPem);
        if (!chainCheck.IsValid) return chainCheck;

        // (e) Verify ECDSA signature over the artifact bytes.
        byte[] signature;
        try { signature = Convert.FromBase64String(bundle.Base64Signature!); }
        catch { return SigstoreVerificationResult.Fail("簽章編碼錯誤"); }

        bool ok;
        using (var ecdsa = leaf.GetECDsaPublicKey())
        {
            if (ecdsa is null) return SigstoreVerificationResult.Fail("憑證無 ECDSA 公鑰");
            ok = ecdsa.VerifyData(
                artifactBytes,
                signature,
                HashAlgorithmName.SHA256,
                DSASignatureFormat.Rfc3279DerSequence);
        }
        if (!ok) return SigstoreVerificationResult.Fail("簽章驗證失敗");

        // (f) Verify Rekor SignedEntryTimestamp. Legacy form: ECDSA P-256 SHA-256 over
        // the canonical JSON of rekorBundle.Payload. Keys appear in alphabetical order:
        // body, integratedTime, logID, logIndex.
        if (string.IsNullOrEmpty(bundle.RekorBundle.SignedEntryTimestamp))
            return SigstoreVerificationResult.Fail("Rekor SET 缺失");

        var payload = bundle.RekorBundle.Payload;
        var canonicalPayload =
            $"{{\"body\":\"{payload.Body}\",\"integratedTime\":{integratedUnix}," +
            $"\"logID\":\"{payload.LogID}\",\"logIndex\":{payload.LogIndex}}}";
        var setBytes = Encoding.UTF8.GetBytes(canonicalPayload);

        byte[] setSig;
        try { setSig = Convert.FromBase64String(bundle.RekorBundle.SignedEntryTimestamp); }
        catch { return SigstoreVerificationResult.Fail("Rekor SET 編碼錯誤"); }

        var rekorKeyBytes = ExtractPublicKeyBytesFromPem(SigstoreRoots.RekorPublicKeyPem);
        if (rekorKeyBytes is null)
            return SigstoreVerificationResult.Fail("Rekor 公鑰格式錯誤");

        try
        {
            using var rekorEcdsa = ECDsa.Create();
            rekorEcdsa.ImportSubjectPublicKeyInfo(rekorKeyBytes, out _);
            if (!rekorEcdsa.VerifyData(setBytes, setSig,
                    HashAlgorithmName.SHA256,
                    DSASignatureFormat.Rfc3279DerSequence))
                return SigstoreVerificationResult.Fail("Rekor SET 驗證失敗");
        }
        catch (Exception ex)
        {
            return SigstoreVerificationResult.Fail($"Rekor 驗證錯誤：{ex.Message}");
        }

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
            using var intermediate = X509Certificate2.CreateFromPem(SigstoreRoots.FulcioIntermediatePem);
            using var chain = new X509Chain();
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.Add(root);
            // Fulcio's short-lived leaf certs are issued by an INTERMEDIATE, not by the
            // root directly. Without supplying the intermediate via ExtraStore the chain
            // builder reports PartialChain / UntrustedRoot ("憑證鏈結無法建立於受信任的根授權").
            chain.ChainPolicy.ExtraStore.Add(intermediate);
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

    private static SigstoreVerificationResult ValidateRekorSet(SigstoreTlogEntry tlog, long integratedUnix)
    {
        if (tlog.InclusionPromise is null || string.IsNullOrWhiteSpace(tlog.InclusionPromise.SignedEntryTimestamp))
            return SigstoreVerificationResult.Fail("缺少 Rekor 簽署時間戳");
        if (tlog.CanonicalizedBody is null || string.IsNullOrWhiteSpace(tlog.CanonicalizedBody.Body))
            return SigstoreVerificationResult.Fail("缺少 Rekor canonicalized body");

        // Rekor signs canonicalised JSON of {body, integratedTime, logID, logIndex}.
        // Keys sorted alphabetically; integers unquoted.
        var setPayload =
            $"{{\"body\":\"{tlog.CanonicalizedBody.Body}\",\"integratedTime\":{integratedUnix}," +
            $"\"logID\":\"{tlog.LogId.KeyId}\",\"logIndex\":{tlog.LogIndex}}}";
        var setBytes = Encoding.UTF8.GetBytes(setPayload);
        byte[] setSignature;
        try { setSignature = Convert.FromBase64String(tlog.InclusionPromise.SignedEntryTimestamp); }
        catch { return SigstoreVerificationResult.Fail("Rekor SET 編碼錯誤"); }

        try
        {
            var rekorKeyBytes = ExtractPublicKeyBytesFromPem(SigstoreRoots.RekorPublicKeyPem);
            if (rekorKeyBytes is null)
                return SigstoreVerificationResult.Fail("Rekor 公鑰格式錯誤");

            // Rekor uses ECDSA P-256 (the "rekor.pub" file is a SubjectPublicKeyInfo).
            using var rekorEcdsa = ECDsa.Create();
            rekorEcdsa.ImportSubjectPublicKeyInfo(rekorKeyBytes, out _);
            if (!rekorEcdsa.VerifyData(setBytes, setSignature,
                    HashAlgorithmName.SHA256,
                    DSASignatureFormat.Rfc3279DerSequence))
                return SigstoreVerificationResult.Fail("Rekor SET 驗證失敗");
        }
        catch (Exception ex)
        {
            return SigstoreVerificationResult.Fail($"Rekor 驗證錯誤：{ex.Message}");
        }
        return SigstoreVerificationResult.Ok();
    }

    private static byte[]? ExtractPublicKeyBytesFromPem(string pem)
    {
        if (string.IsNullOrWhiteSpace(pem) || pem.Contains("replaced-in-phase"))
            return null;
        var lines = pem.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        var body = string.Concat(lines.Where(l => !l.StartsWith("---")));
        try { return Convert.FromBase64String(body); }
        catch { return null; }
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

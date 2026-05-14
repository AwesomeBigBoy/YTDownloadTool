# Phase 3 · Security Layer

**Goal:** Implement `Sha256Verifier`, `Ed25519Verifier`, and `SigstoreVerifier` with NIST/RFC test vectors and Sigstore bundle parsing.

**Prerequisites:** Phase 2 complete (tag `phase-2-domain-complete`).

> **Important architectural note:** `SigstoreVerifier` is the load-bearing component for update security. A full Sigstore implementation includes Fulcio cert chain validation, Rekor transparency log inclusion proof, and OIDC subject matching. For v1 we ship a **minimal correct subset**: validate the embedded bundle's certificate chain to a baked-in Fulcio root, verify the artifact signature against the cert's pubkey, check the cert subject matches the expected GitHub workflow identity, and verify the cert was valid at the Rekor-recorded signing time. If any future Sigstore root rotation happens, we update the baked-in roots and re-release.
>
> Network-side Rekor lookup is out of scope for v1 (we trust the offline bundle). This is documented as a known limitation in Section 10 of the spec.

---

### Task 3.1: `Sha256Verifier`

**Files:**
- Create: `src/YtDlpTool.Domain/Security/Sha256Verifier.cs`
- Create: `tests/YtDlpTool.Domain.Tests/Security/Sha256VerifierTests.cs`

- [ ] **Step 1: Write failing tests with NIST vectors**

```csharp
// tests/YtDlpTool.Domain.Tests/Security/Sha256VerifierTests.cs
using YtDlpTool.Domain.Security;

namespace YtDlpTool.Domain.Tests.Security;

public class Sha256VerifierTests
{
    [Theory]
    // NIST FIPS 180-4 vectors
    [InlineData("", "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855")]
    [InlineData("abc", "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad")]
    [InlineData("abcdbcdecdefdefgefghfghighijhijkijkljklmklmnlmnomnopnopq",
                "248d6a61d20638b8e5c026930c3e6039a33ce45964ff2167f6ecedd419db06c1")]
    public void Compute_MatchesNistVectors(string input, string expectedHex)
    {
        var actual = Sha256Verifier.ComputeHex(System.Text.Encoding.UTF8.GetBytes(input));
        Assert.Equal(expectedHex, actual);
    }

    [Fact]
    public void VerifyFile_MatchesExpectedHash()
    {
        var tmp = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tmp, "abc");
            Assert.True(Sha256Verifier.VerifyFile(tmp,
                "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"));
            Assert.False(Sha256Verifier.VerifyFile(tmp, new string('0', 64)));
        }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public void VerifyFile_CaseInsensitiveHex()
    {
        var tmp = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tmp, "abc");
            Assert.True(Sha256Verifier.VerifyFile(tmp,
                "BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD"));
        }
        finally { File.Delete(tmp); }
    }
}
```

- [ ] **Step 2: Run — expect failure**

- [ ] **Step 3: Implement**

```csharp
// src/YtDlpTool.Domain/Security/Sha256Verifier.cs
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
```

- [ ] **Step 4: Run tests**

```powershell
dotnet test tests/YtDlpTool.Domain.Tests/ --filter "FullyQualifiedName~Sha256VerifierTests"
```
Expected: 5 pass.

- [ ] **Step 5: Commit**

```powershell
git add src/YtDlpTool.Domain/Security/Sha256Verifier.cs tests/YtDlpTool.Domain.Tests/Security/Sha256VerifierTests.cs
git commit -m "feat(security): Sha256Verifier with NIST FIPS 180-4 test vectors"
```

---

### Task 3.2: `Ed25519Verifier` using NSec

**Files:**
- Create: `src/YtDlpTool.Domain/Security/Ed25519Verifier.cs`
- Create: `tests/YtDlpTool.Domain.Tests/Security/Ed25519VerifierTests.cs`

Test vectors from RFC 8032 §7.1.

- [ ] **Step 1: Write failing tests**

```csharp
// tests/YtDlpTool.Domain.Tests/Security/Ed25519VerifierTests.cs
using YtDlpTool.Domain.Security;

namespace YtDlpTool.Domain.Tests.Security;

public class Ed25519VerifierTests
{
    // RFC 8032 §7.1 TEST 1
    private static readonly byte[] Pubkey1 = Convert.FromHexString(
        "d75a980182b10ab7d54bfed3c964073a0ee172f3daa62325af021a68f707511a");
    private static readonly byte[] Message1 = Array.Empty<byte>();
    private static readonly byte[] Signature1 = Convert.FromHexString(
        "e5564300c360ac729086e2cc806e828a84877f1eb8e5d974d873e065224901555fb8821590a33bacc61e39701cf9b46bd25bf5f0595bbe24655141438e7a100b");

    // RFC 8032 §7.1 TEST 2
    private static readonly byte[] Pubkey2 = Convert.FromHexString(
        "3d4017c3e843895a92b70aa74d1b7ebc9c982ccf2ec4968cc0cd55f12af4660c");
    private static readonly byte[] Message2 = new byte[] { 0x72 };
    private static readonly byte[] Signature2 = Convert.FromHexString(
        "92a009a9f0d4cab8720e820b5f642540a2b27b5416503f8fb3762223ebdb69da085ac1e43e15996e458f3613d0f11d8c387b2eaeb4302aeeb00d291612bb0c00");

    [Fact]
    public void Verify_Rfc8032_Vector1_Passes()
    {
        Assert.True(Ed25519Verifier.Verify(Message1, Signature1, Pubkey1));
    }

    [Fact]
    public void Verify_Rfc8032_Vector2_Passes()
    {
        Assert.True(Ed25519Verifier.Verify(Message2, Signature2, Pubkey2));
    }

    [Fact]
    public void Verify_TamperedMessage_Fails()
    {
        var tampered = new byte[] { 0x73 };
        Assert.False(Ed25519Verifier.Verify(tampered, Signature2, Pubkey2));
    }

    [Fact]
    public void Verify_WrongKey_Fails()
    {
        Assert.False(Ed25519Verifier.Verify(Message2, Signature2, Pubkey1));
    }

    [Fact]
    public void Verify_TamperedSignature_Fails()
    {
        var sig = (byte[])Signature2.Clone();
        sig[0] ^= 0x01;
        Assert.False(Ed25519Verifier.Verify(Message2, sig, Pubkey2));
    }

    [Fact]
    public void Verify_WrongLengthSignature_Fails()
    {
        Assert.False(Ed25519Verifier.Verify(Message2, new byte[63], Pubkey2));
    }

    [Fact]
    public void Verify_WrongLengthKey_Fails()
    {
        Assert.False(Ed25519Verifier.Verify(Message2, Signature2, new byte[31]));
    }
}
```

- [ ] **Step 2: Run — expect failure**

- [ ] **Step 3: Implement using NSec**

```csharp
// src/YtDlpTool.Domain/Security/Ed25519Verifier.cs
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
            using var key = PublicKey.Import(Alg, publicKey, KeyBlobFormat.RawPublicKey);
            return Alg.Verify(key, message, signature);
        }
        catch
        {
            return false;
        }
    }
}
```

- [ ] **Step 4: Run tests**

```powershell
dotnet test tests/YtDlpTool.Domain.Tests/ --filter "FullyQualifiedName~Ed25519VerifierTests"
```
Expected: 7 pass.

- [ ] **Step 5: Commit**

```powershell
git add src/YtDlpTool.Domain/Security/Ed25519Verifier.cs tests/YtDlpTool.Domain.Tests/Security/Ed25519VerifierTests.cs
git commit -m "feat(security): Ed25519Verifier (NSec) with RFC 8032 test vectors"
```

---

### Task 3.3: Sigstore types and `SigstoreBundle` parser

**Files:**
- Create: `src/YtDlpTool.Domain/Security/SigstoreBundle.cs`
- Create: `src/YtDlpTool.Domain/Security/SigstoreVerificationResult.cs`
- Create: `src/YtDlpTool.Domain/Security/SigstoreJsonContext.cs`
- Create: `tests/YtDlpTool.Domain.Tests/Security/SigstoreBundleTests.cs`

Sigstore "bundle" format (`.sigstore` file) is JSON with the certificate chain, message signature, and Rekor log entry. We parse it with source-generated JSON.

- [ ] **Step 1: Create types**

```csharp
// src/YtDlpTool.Domain/Security/SigstoreBundle.cs
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
```

```csharp
// src/YtDlpTool.Domain/Security/SigstoreVerificationResult.cs
namespace YtDlpTool.Domain.Security;

public sealed record SigstoreVerificationResult(bool IsValid, string? FailureReason)
{
    public static SigstoreVerificationResult Ok() => new(true, null);
    public static SigstoreVerificationResult Fail(string reason) => new(false, reason);
}
```

```csharp
// src/YtDlpTool.Domain/Security/SigstoreJsonContext.cs
using System.Text.Json.Serialization;

namespace YtDlpTool.Domain.Security;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SigstoreBundle))]
public partial class SigstoreJsonContext : JsonSerializerContext
{
}
```

- [ ] **Step 2: Write parser test**

```csharp
// tests/YtDlpTool.Domain.Tests/Security/SigstoreBundleTests.cs
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
```

- [ ] **Step 3: Build + run test**

```powershell
dotnet test tests/YtDlpTool.Domain.Tests/ --filter "FullyQualifiedName~SigstoreBundleTests"
```
Expected: passes.

- [ ] **Step 4: Commit**

```powershell
git add src/YtDlpTool.Domain/Security/SigstoreBundle.cs src/YtDlpTool.Domain/Security/SigstoreVerificationResult.cs src/YtDlpTool.Domain/Security/SigstoreJsonContext.cs tests/YtDlpTool.Domain.Tests/Security/SigstoreBundleTests.cs
git commit -m "feat(security): Sigstore bundle DTOs + source-gen JSON parsing"
```

---

### Task 3.4: `SigstoreVerifier` — full verification pipeline

**Files:**
- Create: `src/YtDlpTool.Domain/Security/SigstoreVerifier.cs`
- Create: `src/YtDlpTool.Domain/Security/SigstoreRoots.cs`
- Create: `tests/YtDlpTool.Domain.Tests/Security/SigstoreVerifierTests.cs`

`SigstoreVerifier` does:
1. Parse the bundle.
2. Verify the leaf cert chains to the baked-in Fulcio root.
3. Confirm the cert's OIDC subject equals the expected identity string.
4. Verify the signature over the artifact's SHA-256 digest using the leaf cert's pubkey.
5. Confirm the cert was valid at the Rekor `integratedTime`.

Fulcio root and intermediate certs are PEM strings baked into `SigstoreRoots.cs` — when Sigstore rotates roots we update this file and re-release.

- [ ] **Step 1: Create `SigstoreRoots` (placeholders for now — real PEMs added in Phase 10 / first CI run)**

```csharp
// src/YtDlpTool.Domain/Security/SigstoreRoots.cs
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
```

> Until Phase 10 fills these in, the verifier's full-chain path will fail in production. Phase 3 tests verify the *logic*; the *roots* are wired in Phase 10 once we know the exact GitHub workflow identity to bind against.

- [ ] **Step 2: Write tests for verifier logic (with stubbed cert behaviour)**

```csharp
// tests/YtDlpTool.Domain.Tests/Security/SigstoreVerifierTests.cs
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

    // Note: full pass-case tests require a real bundle from the first CI run.
    // Those land in Phase 10 with a regression fixture under tests/fixtures/sigstore/.
}
```

- [ ] **Step 3: Implement**

```csharp
// src/YtDlpTool.Domain/Security/SigstoreVerifier.cs
using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
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

        var certRaw = bundle.VerificationMaterial.Certificate.RawBytes;
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

        var sanText = sanExtension.Format(true);
        var matched = false;
        foreach (var line in sanText.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var idx = line.IndexOf("URI=", StringComparison.OrdinalIgnoreCase);
            if (idx < 0) continue;
            var uri = line[(idx + 4)..].Trim();
            if (sanRegex.IsMatch(uri)) { matched = true; break; }
        }
        if (!matched)
            return SigstoreVerificationResult.Fail("憑證身份不符預期簽署者");

        // Fulcio embeds OIDC issuer as a custom extension OID 1.3.6.1.4.1.57264.1.1 (or .8 in newer versions)
        bool foundIssuer = false;
        foreach (var ext in cert.Extensions)
        {
            if (ext.Oid?.Value is "1.3.6.1.4.1.57264.1.1" or "1.3.6.1.4.1.57264.1.8")
            {
                var raw = System.Text.Encoding.UTF8.GetString(ext.RawData);
                if (raw.Contains(expectedIssuer)) { foundIssuer = true; break; }
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
```

- [ ] **Step 4: Run tests**

```powershell
dotnet test tests/YtDlpTool.Domain.Tests/ --filter "FullyQualifiedName~SigstoreVerifierTests"
```
Expected: 3 negative-path tests pass (positive-path test fixture lands in Phase 10).

- [ ] **Step 5: Commit**

```powershell
git add src/YtDlpTool.Domain/Security/SigstoreRoots.cs src/YtDlpTool.Domain/Security/SigstoreVerifier.cs tests/YtDlpTool.Domain.Tests/Security/SigstoreVerifierTests.cs
git commit -m "feat(security): SigstoreVerifier (SAN, chain, time, signature) — negative-path tested"
```

---

### Task 3.5: Verify full test suite + NativeAOT publish

- [ ] **Step 1: Run all tests**

```powershell
dotnet test
```
Expected: all green.

- [ ] **Step 2: AOT publish**

```powershell
dotnet publish src/YtDlpTool/YtDlpTool.csproj -c Release -r win-x64
```
Expected: succeeds. The Domain project is referenced by the WPF app, so AOT analyzer covers it. Watch for `IL` warnings — there should be none.

- [ ] **Step 3: Tag**

```powershell
git tag phase-3-security-complete
```

---

## Phase 3 complete gate

- [ ] `Sha256Verifier` with NIST test vectors
- [ ] `Ed25519Verifier` with RFC 8032 test vectors + tamper tests
- [ ] `SigstoreBundle` parses with source-gen JSON
- [ ] `SigstoreVerifier` performs SAN/issuer/chain/time/signature checks; positive-path fixture deferred to Phase 10
- [ ] All tests green
- [ ] NativeAOT publish green
- [ ] Tag `phase-3-security-complete`

Proceed to Phase 4.

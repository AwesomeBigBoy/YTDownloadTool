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

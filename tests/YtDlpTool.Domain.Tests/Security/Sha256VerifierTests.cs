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

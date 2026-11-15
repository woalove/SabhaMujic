using SabhaMujic.Core.Security;

namespace SabhaMujic.Core.Tests;

public class SecretHasherTests
{
    [Fact]
    public void Hash_verifies_and_rejects_wrong_secret()
    {
        var encoded = SecretHasher.Hash("1234-pin", iterations: 1_000);
        Assert.True(SecretHasher.Verify("1234-pin", encoded));
        Assert.False(SecretHasher.Verify("1234-pim", encoded));
        Assert.False(SecretHasher.Verify("", encoded));
    }

    [Fact]
    public void Same_secret_gets_different_salts()
    {
        Assert.NotEqual(SecretHasher.Hash("x", 1_000), SecretHasher.Hash("x", 1_000));
    }

    [Fact]
    public void Default_iteration_count_is_used()
    {
        Assert.StartsWith($"pbkdf2-sha256${SecretHasher.DefaultIterations}$", SecretHasher.Hash("x"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("pbkdf2-sha256$abc$AAAA$AAAA")]
    [InlineData("pbkdf2-sha256$0$AAAA$AAAA")]
    [InlineData("pbkdf2-sha256$10$not base64$AAAA")]
    [InlineData("md5$10$AAAA$AAAA")]
    public void Malformed_hashes_never_verify(string encoded) => Assert.False(SecretHasher.Verify("x", encoded));

    [Fact]
    public void Tokens_are_unique_url_safe_and_hash_stably()
    {
        var a = Tokens.NewToken();
        var b = Tokens.NewToken();
        Assert.NotEqual(a, b);
        Assert.Equal(43, a.Length);
        Assert.DoesNotContain('+', a);
        Assert.DoesNotContain('/', a);
        Assert.Equal(Tokens.HashToken(a), Tokens.HashToken(a));
        Assert.NotEqual(Tokens.HashToken(a), Tokens.HashToken(b));
    }

    [Fact]
    public void Pairing_codes_are_six_digits()
    {
        for (var i = 0; i < 200; i++)
        {
            var code = Tokens.NewPairingCode();
            Assert.Equal(6, code.Length);
            Assert.True(code.All(char.IsAsciiDigit));
        }
    }
}

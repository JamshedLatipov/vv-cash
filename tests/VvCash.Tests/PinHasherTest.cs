using System;
using System.Security.Cryptography;
using VvCash.Services;
using Xunit;

namespace VvCash.Tests;

public class PinHasherTest
{
    // Builds an encoded hash the same way the Go backend does, so the test proves
    // cross-language compatibility of the format rather than self-consistency.
    private static string Encode(string pin, int iterations = 1000)
    {
        var salt = new byte[16];
        RandomNumberGenerator.Fill(salt);
        using var kdf = new Rfc2898DeriveBytes(pin, salt, iterations, HashAlgorithmName.SHA256);
        var key = kdf.GetBytes(32);
        return $"pbkdf2_sha256${iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(key)}";
    }

    [Fact]
    public void Verify_AcceptsCorrectPin()
    {
        Assert.True(PinHasher.Verify("4821", Encode("4821")));
    }

    [Fact]
    public void Verify_RejectsWrongPin()
    {
        Assert.False(PinHasher.Verify("4822", Encode("4821")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("nonsense")]
    [InlineData("pbkdf2_sha256$abc$c2FsdA==$aGFzaA==")]
    [InlineData("argon2$1$2$3")]
    [InlineData("pbkdf2_sha256$1000$!!!notbase64!!!$aGFzaA==")]
    public void Verify_RejectsMalformedHash(string encoded)
    {
        Assert.False(PinHasher.Verify("4821", encoded));
    }

    [Fact]
    public void Verify_RejectsEmptyPinAgainstValidHash()
    {
        Assert.False(PinHasher.Verify("", Encode("4821")));
    }
}

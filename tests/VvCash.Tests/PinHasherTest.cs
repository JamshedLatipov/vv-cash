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

    [Fact]
    public void Verify_RejectsIterationCountAboveCeiling()
    {
        // Mirrors the Go backend's TestVerifyPINRejectsIterationAboveCeiling
        // (users/pin_test.go): derives a genuinely correct hash at an iteration
        // count one above the 100_000 * 10 ceiling and checks Verify refuses it
        // because of the ceiling, not because the hash itself is wrong. Without
        // this guard, a corrupted iteration count in the local cache could make
        // Rfc2898DeriveBytes run for minutes with no way to cancel it.
        const int overCeiling = 100_000 * 10 + 1;
        var encoded = Encode("4821", overCeiling);
        Assert.False(PinHasher.Verify("4821", encoded));
    }

    // Generated with the backend's actual users.HashPIN / pbkdf2.Key (users/pin.go),
    // via a temporary throwaway Go test run in the backend worktree
    // (C:\Users\Jamshed\.config\superpowers\worktrees\cloudmarket-server\seller-pin),
    // deleted immediately after and never committed. Unlike Encode() above, this
    // value was not produced by any .NET code, so it proves byte-for-byte agreement
    // between Go's and .NET's PBKDF2-HMAC-SHA256, not merely format agreement.
    // Uses 1000 iterations instead of the production 100000 purely to keep this a
    // small, fast, readable fixture; iteration count does not affect KDF correctness.
    //
    // To regenerate: in the backend worktree's `users` package, add a temporary test
    // calling pbkdf2.Key([]byte("4821"), []byte("0123456789abcdef"), 1000, 32, sha256.New)
    // and print fmt.Sprintf("pbkdf2_sha256$%d$%s$%s", 1000,
    // base64.StdEncoding.EncodeToString(salt), base64.StdEncoding.EncodeToString(key)).
    // Delete the temporary test afterward; do not commit it.
    private const string GoFixturePin = "4821";
    private const string GoFixtureHash =
        "pbkdf2_sha256$1000$MDEyMzQ1Njc4OWFiY2RlZg==$2KIayqyY2tOGgIfvBJMU3moAiod7ZbmLTInqY9WWVEc=";

    [Fact]
    public void Verify_AcceptsRealGoGeneratedFixture()
    {
        Assert.True(PinHasher.Verify(GoFixturePin, GoFixtureHash));
    }

    [Fact]
    public void Verify_RejectsWrongPinAgainstRealGoGeneratedFixture()
    {
        Assert.False(PinHasher.Verify("4822", GoFixtureHash));
    }
}

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
        Assert.Equal(PinVerificationResult.Valid, PinHasher.Verify("4821", Encode("4821")));
    }

    [Fact]
    public void Verify_RejectsWrongPin()
    {
        Assert.Equal(PinVerificationResult.WrongPin, PinHasher.Verify("4822", Encode("4821")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("nonsense")]
    [InlineData("pbkdf2_sha256$abc$c2FsdA==$aGFzaA==")]
    [InlineData("argon2$1$2$3")]
    [InlineData("pbkdf2_sha256$1000$!!!notbase64!!!$aGFzaA==")]
    // iterations == 0: exercises the `iterations <= 0` branch, not just TryParse failure.
    [InlineData("pbkdf2_sha256$0$c2FsdA==$aGFzaA==")]
    // negative iterations: same branch, other side of zero.
    [InlineData("pbkdf2_sha256$-5$c2FsdA==$aGFzaA==")]
    // well-formed prefix/iterations/salt, but the hash segment decodes to zero bytes.
    [InlineData("pbkdf2_sha256$1000$c2FsdA==$")]
    public void Verify_RejectsMalformedHash(string encoded)
    {
        Assert.Equal(PinVerificationResult.Malformed, PinHasher.Verify("4821", encoded));
    }

    [Fact]
    public void Verify_EmptyPinAgainstValidHash_IsWrongPinNotMalformed()
    {
        // encoded is well-formed here, so an empty guess is a non-match, not a
        // corrupt-hash rejection: Malformed must describe the hash, never the guess.
        Assert.Equal(PinVerificationResult.WrongPin, PinHasher.Verify("", Encode("4821")));
    }

    [Fact]
    public void Verify_DistinguishesWrongPinFromMalformedHash()
    {
        // The reason the enum exists: a downstream lockout counter (SellerSession, task
        // 12) must only count WrongPin. A wrong guess against a good hash and a good
        // guess against a corrupt hash must never be reported the same way.
        var validHash = Encode("4821");
        Assert.Equal(PinVerificationResult.WrongPin, PinHasher.Verify("0000", validHash));
        Assert.Equal(PinVerificationResult.Malformed, PinHasher.Verify("4821", "not-a-hash"));
    }

    [Fact]
    public void Verify_RejectsIterationCountAboveCeiling()
    {
        // Mirrors the Go backend's TestVerifyPINRejectsIterationAboveCeiling
        // (users/pin_test.go): derives a genuinely correct hash at an iteration
        // count one above the 100_000 * 10 ceiling and checks Verify refuses it
        // because of the ceiling, not because the hash itself is wrong. Without
        // this guard, a corrupted iteration count in the local cache could make
        // the PBKDF2 derivation run for minutes with no way to cancel it.
        const int overCeiling = 100_000 * 10 + 1;
        var encoded = Encode("4821", overCeiling);
        Assert.Equal(PinVerificationResult.Malformed, PinHasher.Verify("4821", encoded));
    }

    [Fact]
    public void Verify_AcceptsIterationCountExactlyAtCeiling()
    {
        // Paired boundary check for the test above: proves the ceiling comparison is
        // `> PinMaxIterations`, not `>=`. Without this, an off-by-one that rejected the
        // legitimate production value (100_000) scaled up to the ceiling itself would
        // pass the rest of the suite undetected. ~1,000,000 PBKDF2 rounds, still well
        // under a second.
        const int atCeiling = 100_000 * 10;
        var encoded = Encode("4821", atCeiling);
        Assert.Equal(PinVerificationResult.Valid, PinHasher.Verify("4821", encoded));
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
        Assert.Equal(PinVerificationResult.Valid, PinHasher.Verify(GoFixturePin, GoFixtureHash));
    }

    [Fact]
    public void Verify_RejectsWrongPinAgainstRealGoGeneratedFixture()
    {
        Assert.Equal(PinVerificationResult.WrongPin, PinHasher.Verify("4822", GoFixtureHash));
    }
}

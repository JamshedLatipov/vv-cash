using System;
using System.Security.Cryptography;

namespace VvCash.Services;

/// <summary>Outcome of <see cref="PinHasher.Verify"/>.
///
/// Consumers building lockout logic (e.g. a future SellerSession) must only count
/// <see cref="WrongPin"/> toward a failed-attempt counter. <see cref="Malformed"/> is
/// entirely a property of the stored <c>encoded</c> hash, not of what the seller typed:
/// it means the cached row is unusable (torn write, bad migration, tampering) and no PIN
/// can ever verify against it. That must not count as a guess — it should instead trigger
/// a roster refresh so a fresh hash gets cached.</summary>
public enum PinVerificationResult
{
    /// <summary>The PIN matches the hash.</summary>
    Valid,

    /// <summary>The encoded hash was well-formed and trustworthy, but this PIN (including
    /// a missing/empty one) did not match it. Safe to count toward a per-seller lockout.</summary>
    WrongPin,

    /// <summary>The encoded hash itself could not be trusted: wrong shape, unknown
    /// algorithm label, non-base64 salt/hash, an empty decoded hash, or an iteration count
    /// that is zero, negative, or above <see cref="PinHasher"/>'s ceiling. Not the seller's
    /// fault, and retrying the same PIN cannot fix it.</summary>
    Malformed
}

/// <summary>Verifies PBKDF2-HMAC-SHA256 PIN hashes produced by the backend.
/// Format: pbkdf2_sha256$iterations$base64salt$base64hash.
/// Verify only — the cash register never creates hashes, the server does.</summary>
public static class PinHasher
{
    // Production iteration count used by the backend's HashPIN (users/pin.go). Kept
    // here only as the base for PinMaxIterations below, not to derive hashes with.
    private const int ProductionIterations = 100000;

    // Mirrors the backend's pinMaxIterations guard (users/pin.go). encoded is read
    // from an unencrypted local SQLite cache, which is trivially editable, so it is
    // untrusted input here even though it is not attacker-supplied over the network.
    // Without this ceiling, a corrupted or tampered iteration count (e.g. in the
    // billions) would make the PBKDF2 derivation run synchronously for minutes with
    // no timeout or cancellation, freezing the register's UI on PIN entry.
    private const int PinMaxIterations = ProductionIterations * 10;

    public static PinVerificationResult Verify(string? pin, string? encoded)
    {
        // Every check up to the KDF call is validating `encoded` alone, so every
        // rejection here is Malformed regardless of what `pin` is.
        if (string.IsNullOrEmpty(encoded)) return PinVerificationResult.Malformed;

        var parts = encoded.Split('$');
        if (parts.Length != 4 || parts[0] != "pbkdf2_sha256") return PinVerificationResult.Malformed;
        if (!int.TryParse(parts[1], out var iterations) || iterations <= 0 || iterations > PinMaxIterations)
            return PinVerificationResult.Malformed;

        byte[] salt, want;
        try
        {
            salt = Convert.FromBase64String(parts[2]);
            want = Convert.FromBase64String(parts[3]);
        }
        catch (FormatException)
        {
            return PinVerificationResult.Malformed;
        }

        if (want.Length == 0) return PinVerificationResult.Malformed;

        // encoded is trustworthy from here on. A missing/empty pin can never match a
        // real hash, so treat it the same as any other non-matching guess rather than
        // running it through the KDF (also sidesteps passing null into Pbkdf2 below).
        if (string.IsNullOrEmpty(pin)) return PinVerificationResult.WrongPin;

        var got = Rfc2898DeriveBytes.Pbkdf2(pin, salt, iterations, HashAlgorithmName.SHA256, want.Length);
        return CryptographicOperations.FixedTimeEquals(got, want)
            ? PinVerificationResult.Valid
            : PinVerificationResult.WrongPin;
    }
}

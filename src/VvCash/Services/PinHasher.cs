using System;
using System.Security.Cryptography;

namespace VvCash.Services;

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
    // billions) would make Rfc2898DeriveBytes.GetBytes run synchronously for minutes
    // with no timeout or cancellation, freezing the register's UI on PIN entry.
    private const int PinMaxIterations = ProductionIterations * 10;

    public static bool Verify(string? pin, string? encoded)
    {
        if (string.IsNullOrEmpty(pin) || string.IsNullOrEmpty(encoded)) return false;

        var parts = encoded.Split('$');
        if (parts.Length != 4 || parts[0] != "pbkdf2_sha256") return false;
        if (!int.TryParse(parts[1], out var iterations) || iterations <= 0 || iterations > PinMaxIterations) return false;

        byte[] salt, want;
        try
        {
            salt = Convert.FromBase64String(parts[2]);
            want = Convert.FromBase64String(parts[3]);
        }
        catch (FormatException)
        {
            return false;
        }

        if (want.Length == 0) return false;

        using var kdf = new Rfc2898DeriveBytes(pin, salt, iterations, HashAlgorithmName.SHA256);
        var got = kdf.GetBytes(want.Length);
        return CryptographicOperations.FixedTimeEquals(got, want);
    }
}

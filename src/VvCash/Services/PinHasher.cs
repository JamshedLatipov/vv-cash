using System;
using System.Security.Cryptography;

namespace VvCash.Services;

/// <summary>Verifies PBKDF2-HMAC-SHA256 PIN hashes produced by the backend.
/// Format: pbkdf2_sha256$iterations$base64salt$base64hash.
/// Verify only — the cash register never creates hashes, the server does.</summary>
public static class PinHasher
{
    public static bool Verify(string? pin, string? encoded)
    {
        if (string.IsNullOrEmpty(pin) || string.IsNullOrEmpty(encoded)) return false;

        var parts = encoded.Split('$');
        if (parts.Length != 4 || parts[0] != "pbkdf2_sha256") return false;
        if (!int.TryParse(parts[1], out var iterations) || iterations <= 0) return false;

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

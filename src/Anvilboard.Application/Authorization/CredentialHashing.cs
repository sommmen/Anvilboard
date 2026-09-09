using System.Security.Cryptography;
using System.Text;

namespace Anvilboard.Application.Authorization;

/// <summary>
/// Hashes workspace-scoped API token bearer values for storage/lookup (NFR-SEC-001). Tokens are
/// high-entropy random values, so a plain deterministic hash (no per-value salt) is safe and lets
/// <see cref="WorkspaceAuthorizationService"/> look a presented token up by its hash directly,
/// rather than iterating every stored token to compare with a salted algorithm.
/// </summary>
public static class TokenHasher
{
    public static string Hash(string rawToken) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawToken)));
}

/// <summary>
/// Hashes and verifies local human passwords (NFR-SEC-001) using PBKDF2 with a per-password
/// random salt, since passwords (unlike API tokens) are low-entropy and must never be compared
/// with an unsalted, lookup-friendly hash.
/// </summary>
public static class PasswordHasher
{
    private const int Iterations = 100_000;
    private const int SaltSizeBytes = 16;
    private const int KeySizeBytes = 32;

    /// <summary>Produces a self-describing "{iterations}.{salt}.{key}" string, so the iteration
    /// count can be raised in the future without invalidating already-hashed passwords.</summary>
    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSizeBytes);
        var key = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, Iterations, HashAlgorithmName.SHA256, KeySizeBytes);
        return $"{Iterations}.{Convert.ToBase64String(salt)}.{Convert.ToBase64String(key)}";
    }

    public static bool Verify(string password, string hash)
    {
        var parts = hash.Split('.');
        if (parts.Length != 3 || !int.TryParse(parts[0], out var iterations))
        {
            return false;
        }

        byte[] salt;
        byte[] expectedKey;
        try
        {
            salt = Convert.FromBase64String(parts[1]);
            expectedKey = Convert.FromBase64String(parts[2]);
        }
        catch (FormatException)
        {
            return false;
        }

        var actualKey = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, expectedKey.Length);
        return CryptographicOperations.FixedTimeEquals(actualKey, expectedKey);
    }
}

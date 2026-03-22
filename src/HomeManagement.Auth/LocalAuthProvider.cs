using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace HomeManagement.Auth;

/// <summary>
/// Authenticates users against the local database using Argon2id password hashing.
/// </summary>
public sealed class LocalAuthProvider
{
    private readonly ILogger<LocalAuthProvider> _logger;

    public LocalAuthProvider(ILogger<LocalAuthProvider> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Hash a password using Argon2id with a random salt.
    /// Returns a combined string: $argon2id${salt}${hash}
    /// </summary>
    public static string HashPassword(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = ComputeArgon2(password, salt);
        return $"$argon2id${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    /// <summary>
    /// Verify a password against a stored hash.
    /// </summary>
    public static bool VerifyPassword(string password, string storedHash)
    {
        var parts = storedHash.Split('$', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3 || parts[0] != "argon2id")
            return false;

        var salt = Convert.FromBase64String(parts[1]);
        var expectedHash = Convert.FromBase64String(parts[2]);
        var computedHash = ComputeArgon2(password, salt);

        return CryptographicOperations.FixedTimeEquals(computedHash, expectedHash);
    }

    private static byte[] ComputeArgon2(string password, byte[] salt)
    {
        using var argon2 = new Argon2id(Encoding.UTF8.GetBytes(password));
        argon2.Salt = salt;
        argon2.DegreeOfParallelism = 4;
        argon2.MemorySize = 65536; // 64 MB
        argon2.Iterations = 3;
        return argon2.GetBytes(32);
    }
}

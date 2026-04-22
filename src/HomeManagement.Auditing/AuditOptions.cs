using System.ComponentModel.DataAnnotations;

namespace HomeManagement.Auditing;

/// <summary>
/// Configuration options for the audit subsystem.
/// </summary>
public sealed class AuditOptions
{
    public const string SectionName = "Audit";

    /// <summary>
    /// Base64-encoded 32-byte (256-bit) HMAC-SHA256 key for audit chain integrity.
    /// Generate with: Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
    /// Must be stored in a secret store (environment variable, secrets manager) — never in source code.
    /// </summary>
    [Required(ErrorMessage = "Audit:HmacKey is required. Generate with: Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))")]
    [MinLength(44, ErrorMessage = "Audit:HmacKey must be base64 of at least 32 bytes (44+ base64 characters).")]
    public string HmacKey { get; set; } = string.Empty;

    /// <summary>Decodes and validates the HMAC key bytes.</summary>
    /// <exception cref="InvalidOperationException">Thrown if the key is too short after decoding.</exception>
    public byte[] GetKeyBytes()
    {
        var key = Convert.FromBase64String(HmacKey);
        if (key.Length < 32)
            throw new InvalidOperationException(
                "Audit:HmacKey must decode to at least 32 bytes. Re-generate with: Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))");
        return key;
    }
}

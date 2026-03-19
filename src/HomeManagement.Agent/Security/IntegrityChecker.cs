using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using NSec.Cryptography;

namespace HomeManagement.Agent.Security;

/// <summary>
/// Verifies the integrity of downloaded update packages using SHA-256 and
/// Ed25519 signatures.
/// </summary>
public sealed class IntegrityChecker(ILogger<IntegrityChecker> logger)
{
    private static readonly SignatureAlgorithm Ed25519 = SignatureAlgorithm.Ed25519;

    /// <summary>
    /// Verifies the SHA-256 hash of a file against the expected value.
    /// </summary>
    public async Task<bool> VerifySha256Async(string filePath, ReadOnlyMemory<byte> expectedHash, CancellationToken ct = default)
    {
        await using var stream = File.OpenRead(filePath);
        var actualHash = await SHA256.HashDataAsync(stream, ct);

        var match = actualHash.AsSpan().SequenceEqual(expectedHash.Span);
        if (!match)
        {
            logger.LogError(
                "SHA-256 mismatch for {File}: expected {Expected}, got {Actual}",
                filePath,
                Convert.ToHexString(expectedHash.Span),
                Convert.ToHexString(actualHash));
        }

        return match;
    }

    /// <summary>
    /// Verifies an Ed25519 signature over a file using the supplied public key bytes.
    /// The public key is expected to be a raw 32-byte Ed25519 public key.
    /// </summary>
    public async Task<bool> VerifyEd25519Async(
        string filePath,
        ReadOnlyMemory<byte> signature,
        ReadOnlyMemory<byte> publicKeyBytes,
        CancellationToken ct = default)
    {
        if (publicKeyBytes.Length != Ed25519.PublicKeySize)
        {
            logger.LogError("Ed25519 public key has invalid length {Length} (expected {Expected})",
                publicKeyBytes.Length, Ed25519.PublicKeySize);
            return false;
        }

        if (signature.Length != Ed25519.SignatureSize)
        {
            logger.LogError("Ed25519 signature has invalid length {Length} (expected {Expected})",
                signature.Length, Ed25519.SignatureSize);
            return false;
        }

        try
        {
            var publicKey = PublicKey.Import(Ed25519, publicKeyBytes.Span, KeyBlobFormat.RawPublicKey);

            var fileData = await File.ReadAllBytesAsync(filePath, ct);
            var valid = Ed25519.Verify(publicKey, fileData, signature.Span);

            if (!valid)
            {
                logger.LogError("Ed25519 signature verification failed for {File}", filePath);
            }

            return valid;
        }
        catch (FormatException ex)
        {
            logger.LogError(ex, "Invalid Ed25519 public key format");
            return false;
        }
    }
}

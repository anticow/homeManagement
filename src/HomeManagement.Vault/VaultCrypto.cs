using System.Security.Cryptography;
using Konscious.Security.Cryptography;

namespace HomeManagement.Vault;

/// <summary>
/// AES-256-GCM encryption with Argon2id key derivation for the credential vault.
/// </summary>
internal static class VaultCrypto
{
    private const int SaltLength = 32;
    private const int NonceLength = 12;  // AES-GCM nonce
    private const int TagLength = 16;    // AES-GCM auth tag
    private const int KeyLength = 32;    // AES-256

    // Argon2id parameters per architecture spec
    private const int Argon2MemoryKiB = 65536; // 64 MiB
    private const int Argon2Iterations = 3;
    private const int Argon2Parallelism = 4;

    /// <summary>
    /// Derive an AES-256 key from a master password using Argon2id.
    /// </summary>
    public static byte[] DeriveKey(ReadOnlySpan<byte> password, byte[] salt)
    {
        using var argon2 = new Argon2id(password.ToArray());
        argon2.Salt = salt;
        argon2.MemorySize = Argon2MemoryKiB;
        argon2.Iterations = Argon2Iterations;
        argon2.DegreeOfParallelism = Argon2Parallelism;
        return argon2.GetBytes(KeyLength);
    }

    /// <summary>
    /// Encrypt plaintext using AES-256-GCM. Returns salt + nonce + ciphertext + tag.
    /// </summary>
    public static byte[] Encrypt(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltLength);
        var key = DeriveKey(password, salt);
        try
        {
            var nonce = RandomNumberGenerator.GetBytes(NonceLength);
            var ciphertext = new byte[plaintext.Length];
            var tag = new byte[TagLength];

            using var aes = new AesGcm(key, TagLength);
            aes.Encrypt(nonce, plaintext, ciphertext, tag);

            // Layout: [salt][nonce][ciphertext][tag]
            var result = new byte[SaltLength + NonceLength + ciphertext.Length + TagLength];
            salt.CopyTo(result, 0);
            nonce.CopyTo(result, SaltLength);
            ciphertext.CopyTo(result.AsSpan(SaltLength + NonceLength));
            tag.CopyTo(result, SaltLength + NonceLength + ciphertext.Length);

            return result;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <summary>
    /// Decrypt a blob previously produced by <see cref="Encrypt"/>.
    /// </summary>
    public static byte[] Decrypt(ReadOnlySpan<byte> encrypted, ReadOnlySpan<byte> password)
    {
        if (encrypted.Length < SaltLength + NonceLength + TagLength)
            throw new CryptographicException("Encrypted data is too short.");

        var salt = encrypted[..SaltLength].ToArray();
        var nonce = encrypted.Slice(SaltLength, NonceLength);
        var ciphertextLength = encrypted.Length - SaltLength - NonceLength - TagLength;
        var ciphertext = encrypted.Slice(SaltLength + NonceLength, ciphertextLength);
        var tag = encrypted.Slice(SaltLength + NonceLength + ciphertextLength, TagLength);

        var key = DeriveKey(password, salt);
        try
        {
            var plaintext = new byte[ciphertextLength];
            using var aes = new AesGcm(key, TagLength);
            aes.Decrypt(nonce, ciphertext, tag, plaintext);
            return plaintext;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <summary>
    /// Encrypt plaintext directly with a pre-derived AES-256 key (no KDF pass).
    /// Returns nonce + ciphertext + tag. Use for bulk vault persistence where
    /// the key is already derived via <see cref="DeriveKey"/>.
    /// </summary>
    internal static byte[] EncryptDirect(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> key)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceLength);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagLength];

        using var aes = new AesGcm(key, TagLength);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);

        // Layout: [nonce][ciphertext][tag]
        var result = new byte[NonceLength + ciphertext.Length + TagLength];
        nonce.CopyTo(result, 0);
        ciphertext.CopyTo(result.AsSpan(NonceLength));
        tag.CopyTo(result, NonceLength + ciphertext.Length);
        return result;
    }

    /// <summary>
    /// Decrypt a blob previously produced by <see cref="EncryptDirect"/>.
    /// </summary>
    internal static byte[] DecryptDirect(ReadOnlySpan<byte> encrypted, ReadOnlySpan<byte> key)
    {
        if (encrypted.Length < NonceLength + TagLength)
            throw new CryptographicException("Encrypted data is too short.");

        var nonce = encrypted[..NonceLength];
        var ciphertextLength = encrypted.Length - NonceLength - TagLength;
        var ciphertext = encrypted.Slice(NonceLength, ciphertextLength);
        var tag = encrypted.Slice(NonceLength + ciphertextLength, TagLength);

        var plaintext = new byte[ciphertextLength];
        using var aes = new AesGcm(key, TagLength);
        aes.Decrypt(nonce, ciphertext, tag, plaintext);
        return plaintext;
    }
}

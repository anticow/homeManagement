using System.Security;
using HomeManagement.Abstractions.Models;

namespace HomeManagement.Abstractions.Interfaces;

/// <summary>
/// Securely stores, retrieves, and manages credentials for remote machine authentication.
/// Credentials are encrypted at rest and only decrypted when actively needed.
/// </summary>
public interface ICredentialVault
{
    Task UnlockAsync(SecureString masterPassword, CancellationToken ct = default);
    Task LockAsync(CancellationToken ct = default);
    bool IsUnlocked { get; }

    /// <summary>
    /// Emits the current lock state whenever it changes (true = unlocked, false = locked).
    /// Subscribers receive an initial value on subscription.
    /// </summary>
    IObservable<bool> LockStateChanged { get; }

    Task<CredentialEntry> AddAsync(CredentialCreateRequest request, CancellationToken ct = default);
    Task<CredentialEntry> UpdateAsync(Guid id, CredentialUpdateRequest request, CancellationToken ct = default);
    Task RemoveAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<CredentialEntry>> ListAsync(CancellationToken ct = default);

    /// <summary>
    /// Retrieves the decrypted credential payload. The caller must dispose/zero the payload promptly.
    /// </summary>
    Task<CredentialPayload> GetPayloadAsync(Guid id, CancellationToken ct = default);

    Task RotateEncryptionKeyAsync(SecureString newMasterPassword, CancellationToken ct = default);
    Task<byte[]> ExportAsync(CancellationToken ct = default);
    Task ImportAsync(byte[] encryptedBlob, SecureString password, CancellationToken ct = default);
}

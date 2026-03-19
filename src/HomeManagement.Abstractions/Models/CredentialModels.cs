using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace HomeManagement.Abstractions.Models;

// ── Credentials ──

public record CredentialEntry(
    Guid Id,
    string Label,
    CredentialType Type,
    string Username,
    Guid[] AssociatedMachineIds,
    DateTime CreatedUtc,
    DateTime? LastUsedUtc,
    DateTime? LastRotatedUtc);

public record CredentialCreateRequest(
    string Label,
    CredentialType Type,
    string Username,
    byte[] Payload);

public record CredentialUpdateRequest(
    string? Label = null,
    string? Username = null,
    byte[]? Payload = null);

/// <summary>
/// A decrypted credential payload. MUST be disposed after use to zero sensitive memory.
/// Wrap usage in a <c>using</c> block or statement.
/// </summary>
public sealed class CredentialPayload : IDisposable
{
    private GCHandle _handle;
    private bool _disposed;

    public CredentialType Type { get; }
    public string Username { get; }

    private readonly byte[] _decryptedPayload;

    public CredentialPayload(CredentialType type, string username, byte[] decryptedPayload)
    {
        Type = type;
        Username = username;
        _decryptedPayload = decryptedPayload;
        _handle = GCHandle.Alloc(_decryptedPayload, GCHandleType.Pinned);
    }

    public ReadOnlySpan<byte> DecryptedPayload
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _decryptedPayload;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Zero the buffer before releasing
        CryptographicOperations.ZeroMemory(_decryptedPayload);

        if (_handle.IsAllocated)
            _handle.Free();
    }
}

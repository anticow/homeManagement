using System.Collections.Concurrent;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HomeManagement.Abstractions;
using HomeManagement.Abstractions.Interfaces;
using HomeManagement.Abstractions.Models;
using Microsoft.Extensions.Logging;

namespace HomeManagement.Vault;

/// <summary>
/// Encrypted credential vault. Credentials are encrypted at rest (AES-256-GCM, Argon2id KDF)
/// and only decrypted when actively needed. All ops require the vault to be unlocked first.
/// </summary>
internal sealed class CredentialVaultService : ICredentialVault, IDisposable
{
    private readonly ILogger<CredentialVaultService> _logger;
    private readonly ConcurrentDictionary<Guid, VaultEntry> _entries = new();
    private byte[]? _derivedKey;
    private readonly object _lock = new();
    private Timer? _idleLockTimer;
    private readonly TimeSpan _idleTimeout = TimeSpan.FromMinutes(15);
    private readonly BehaviorSubject<bool> _lockStateSubject = new(false);

    public IObservable<bool> LockStateChanged => _lockStateSubject.AsObservable().DistinctUntilChanged();

    public CredentialVaultService(ILogger<CredentialVaultService> logger)
    {
        _logger = logger;
    }

    public bool IsUnlocked => _derivedKey is not null;

    public Task UnlockAsync(SecureString masterPassword, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var passwordBytes = SecureStringToBytes(masterPassword);
        try
        {
            // Derive master key — held in memory while vault is unlocked
            var salt = GetOrCreateMasterSalt();
            var key = VaultCrypto.DeriveKey(passwordBytes, salt);

            lock (_lock)
            {
                if (_derivedKey is not null)
                    CryptographicOperations.ZeroMemory(_derivedKey);
                _derivedKey = key;
            }

            _logger.LogInformation("Credential vault unlocked");
            _lockStateSubject.OnNext(true);
            ResetIdleTimer();
            return Task.CompletedTask;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordBytes);
        }
    }

    public Task LockAsync(CancellationToken ct = default)
    {
        lock (_lock)
        {
            _idleLockTimer?.Dispose();
            _idleLockTimer = null;

            if (_derivedKey is not null)
            {
                CryptographicOperations.ZeroMemory(_derivedKey);
                _derivedKey = null;
            }
        }

        _logger.LogInformation("Credential vault locked");
        _lockStateSubject.OnNext(false);
        return Task.CompletedTask;
    }

    public Task<CredentialEntry> AddAsync(CredentialCreateRequest request, CancellationToken ct = default)
    {
        EnsureUnlocked();
        ct.ThrowIfCancellationRequested();

        var id = Guid.NewGuid();
        var now = DateTime.UtcNow;

        var encryptedPayload = VaultCrypto.Encrypt(request.Payload, _derivedKey!);

        var entry = new VaultEntry
        {
            Id = id,
            Label = request.Label,
            Type = request.Type,
            Username = request.Username,
            EncryptedPayload = encryptedPayload,
            AssociatedMachineIds = [],
            CreatedUtc = now,
        };

        _entries[id] = entry;
        _logger.LogInformation("Credential {CredentialId} added (type={Type})", id, request.Type);

        return Task.FromResult(ToCredentialEntry(entry));
    }

    public Task<CredentialEntry> UpdateAsync(Guid id, CredentialUpdateRequest request, CancellationToken ct = default)
    {
        EnsureUnlocked();
        ct.ThrowIfCancellationRequested();

        if (!_entries.TryGetValue(id, out var entry))
            throw new KeyNotFoundException($"Credential {id} not found.");

        if (request.Label is not null) entry.Label = request.Label;
        if (request.Username is not null) entry.Username = request.Username;

        if (request.Payload is not null)
        {
            entry.EncryptedPayload = VaultCrypto.Encrypt(request.Payload, _derivedKey!);
        }

        _logger.LogInformation("Credential {CredentialId} updated", id);
        return Task.FromResult(ToCredentialEntry(entry));
    }

    public Task RemoveAsync(Guid id, CancellationToken ct = default)
    {
        EnsureUnlocked();
        ct.ThrowIfCancellationRequested();

        if (!_entries.TryRemove(id, out var entry))
            throw new KeyNotFoundException($"Credential {id} not found.");

        // Zero encrypted payload in memory
        CryptographicOperations.ZeroMemory(entry.EncryptedPayload);
        _logger.LogInformation("Credential {CredentialId} removed", id);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<CredentialEntry>> ListAsync(CancellationToken ct = default)
    {
        EnsureUnlocked();
        ct.ThrowIfCancellationRequested();

        IReadOnlyList<CredentialEntry> result = _entries.Values
            .Select(ToCredentialEntry)
            .OrderBy(c => c.Label)
            .ToList();

        return Task.FromResult(result);
    }

    public Task<CredentialPayload> GetPayloadAsync(Guid id, CancellationToken ct = default)
    {
        EnsureUnlocked();
        ct.ThrowIfCancellationRequested();

        if (!_entries.TryGetValue(id, out var entry))
            throw new KeyNotFoundException($"Credential {id} not found.");

        var decrypted = VaultCrypto.Decrypt(entry.EncryptedPayload, _derivedKey!);
        entry.LastUsedUtc = DateTime.UtcNow;
        ResetIdleTimer();

        _logger.LogInformation("Credential {CredentialId} payload accessed", id);
        return Task.FromResult(new CredentialPayload(entry.Type, entry.Username, decrypted));
    }

    public Task RotateEncryptionKeyAsync(SecureString newMasterPassword, CancellationToken ct = default)
    {
        EnsureUnlocked();
        ct.ThrowIfCancellationRequested();

        var newPasswordBytes = SecureStringToBytes(newMasterPassword);
        try
        {
            var newSalt = RandomNumberGenerator.GetBytes(32);
            var newKey = VaultCrypto.DeriveKey(newPasswordBytes, newSalt);

            // Re-encrypt every credential with the new key
            foreach (var entry in _entries.Values)
            {
                var plaintext = VaultCrypto.Decrypt(entry.EncryptedPayload, _derivedKey!);
                try
                {
                    var reEncrypted = VaultCrypto.Encrypt(plaintext, newKey);
                    CryptographicOperations.ZeroMemory(entry.EncryptedPayload);
                    entry.EncryptedPayload = reEncrypted;
                    entry.LastRotatedUtc = DateTime.UtcNow;
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(plaintext);
                }
            }

            lock (_lock)
            {
                CryptographicOperations.ZeroMemory(_derivedKey!);
                _derivedKey = newKey;
            }

            _logger.LogInformation("Vault encryption key rotated, {Count} credentials re-encrypted", _entries.Count);
            return Task.CompletedTask;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(newPasswordBytes);
        }
    }

    public Task<byte[]> ExportAsync(CancellationToken ct = default)
    {
        EnsureUnlocked();
        ct.ThrowIfCancellationRequested();

        var exportData = _entries.Values.Select(e => new VaultExportEntry
        {
            Id = e.Id,
            Label = e.Label,
            Type = e.Type,
            Username = e.Username,
            EncryptedPayload = e.EncryptedPayload,
            AssociatedMachineIds = e.AssociatedMachineIds,
            CreatedUtc = e.CreatedUtc,
        }).ToList();

        var json = JsonSerializer.SerializeToUtf8Bytes(exportData);
        // Encrypt the entire export blob with the current master key
        var encrypted = VaultCrypto.Encrypt(json, _derivedKey!);
        CryptographicOperations.ZeroMemory(json);

        _logger.LogInformation("Vault exported ({Count} credentials)", _entries.Count);
        return Task.FromResult(encrypted);
    }

    public Task ImportAsync(byte[] encryptedBlob, SecureString password, CancellationToken ct = default)
    {
        EnsureUnlocked();
        ct.ThrowIfCancellationRequested();

        var passwordBytes = SecureStringToBytes(password);
        try
        {
            var salt = GetOrCreateMasterSalt();
            var importKey = VaultCrypto.DeriveKey(passwordBytes, salt);
            byte[] decrypted;
            try
            {
                decrypted = VaultCrypto.Decrypt(encryptedBlob, importKey);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(importKey);
            }

            var entries = JsonSerializer.Deserialize<List<VaultExportEntry>>(decrypted)
                ?? throw new InvalidOperationException("Invalid vault export format.");
            CryptographicOperations.ZeroMemory(decrypted);

            foreach (var imported in entries)
            {
                // Re-encrypt with current key
                var plaintext = VaultCrypto.Decrypt(imported.EncryptedPayload, importKey);
                try
                {
                    var reEncrypted = VaultCrypto.Encrypt(plaintext, _derivedKey!);
                    _entries[imported.Id] = new VaultEntry
                    {
                        Id = imported.Id,
                        Label = imported.Label,
                        Type = imported.Type,
                        Username = imported.Username,
                        EncryptedPayload = reEncrypted,
                        AssociatedMachineIds = imported.AssociatedMachineIds,
                        CreatedUtc = imported.CreatedUtc,
                    };
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(plaintext);
                }
            }

            _logger.LogInformation("Vault imported ({Count} credentials)", entries.Count);
            return Task.CompletedTask;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordBytes);
        }
    }

    private void ResetIdleTimer()
    {
        _idleLockTimer?.Dispose();
        _idleLockTimer = new Timer(_ =>
        {
            _logger.LogInformation("Vault idle timeout ({Timeout}) reached — auto-locking", _idleTimeout);
            _ = LockAsync();
        }, null, _idleTimeout, Timeout.InfiniteTimeSpan);
    }

    public void Dispose()
    {
        _idleLockTimer?.Dispose();
        _lockStateSubject.Dispose();
        lock (_lock)
        {
            if (_derivedKey is not null)
            {
                CryptographicOperations.ZeroMemory(_derivedKey);
                _derivedKey = null;
            }
        }
    }

    private void EnsureUnlocked()
    {
        if (!IsUnlocked)
            throw new InvalidOperationException("Vault is locked. Call UnlockAsync before accessing credentials.");
    }

    private static byte[] GetOrCreateMasterSalt()
    {
        // In production, this would be persisted alongside the vault file.
        // For now, use a deterministic salt from the application data directory.
        return SHA256.HashData("HomeManagement.Vault.MasterSalt"u8);
    }

    private static byte[] SecureStringToBytes(SecureString secureString)
    {
        var ptr = Marshal.SecureStringToGlobalAllocUnicode(secureString);
        try
        {
            var chars = new char[secureString.Length];
            Marshal.Copy(ptr, chars, 0, secureString.Length);
            var bytes = Encoding.UTF8.GetBytes(chars);
            Array.Clear(chars, 0, chars.Length);
            return bytes;
        }
        finally
        {
            Marshal.ZeroFreeGlobalAllocUnicode(ptr);
        }
    }

    private static CredentialEntry ToCredentialEntry(VaultEntry e) => new(
        e.Id, e.Label, e.Type, e.Username,
        e.AssociatedMachineIds, e.CreatedUtc,
        e.LastUsedUtc, e.LastRotatedUtc);

    private sealed class VaultEntry
    {
        public Guid Id { get; set; }
        public string Label { get; set; } = string.Empty;
        public CredentialType Type { get; set; }
        public string Username { get; set; } = string.Empty;
        public byte[] EncryptedPayload { get; set; } = [];
        public Guid[] AssociatedMachineIds { get; set; } = [];
        public DateTime CreatedUtc { get; set; }
        public DateTime? LastUsedUtc { get; set; }
        public DateTime? LastRotatedUtc { get; set; }
    }

    private sealed class VaultExportEntry
    {
        public Guid Id { get; set; }
        public string Label { get; set; } = string.Empty;
        public CredentialType Type { get; set; }
        public string Username { get; set; } = string.Empty;
        public byte[] EncryptedPayload { get; set; } = [];
        public Guid[] AssociatedMachineIds { get; set; } = [];
        public DateTime CreatedUtc { get; set; }
    }
}

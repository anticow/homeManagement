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
using Microsoft.Extensions.Options;

namespace HomeManagement.Vault;

/// <summary>
/// Encrypted credential vault — AES-256-GCM / Argon2id KDF.
/// <para>
/// Credentials are persisted to an encrypted vault file (MED-6 fix).
/// The master salt is randomly generated on first use and stored in the vault
/// file header, eliminating the previous deterministic SHA-256 salt (CRIT-3 fix).
/// </para>
/// </summary>
internal sealed class CredentialVaultService : ICredentialVault, IDisposable
{
    private readonly ILogger<CredentialVaultService> _logger;
    private readonly string _vaultFilePath;
    private readonly SemaphoreSlim _persistLock = new(1, 1);
    private readonly ConcurrentDictionary<Guid, VaultEntry> _entries = new();
    private byte[]? _derivedKey;
    private byte[]? _masterSalt;
    private readonly object _lock = new();
    private Timer? _idleLockTimer;
    private readonly TimeSpan _idleTimeout = TimeSpan.FromMinutes(15);
    private readonly BehaviorSubject<bool> _lockStateSubject = new(false);

    public IObservable<bool> LockStateChanged => _lockStateSubject.AsObservable().DistinctUntilChanged();

    public CredentialVaultService(ILogger<CredentialVaultService> logger, IOptions<VaultOptions> options)
    {
        _logger = logger;
        var storagePath = options.Value.StoragePath;
        Directory.CreateDirectory(storagePath);
        _vaultFilePath = Path.Combine(storagePath, "vault.dat");
    }

    public bool IsUnlocked => _derivedKey is not null;

    public async Task UnlockAsync(SecureString masterPassword, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var passwordBytes = SecureStringToBytes(masterPassword);
        try
        {
            var vaultFile = await ReadOrInitVaultFileAsync(ct);
            var salt = Convert.FromBase64String(vaultFile.MasterSalt);
            var key = VaultCrypto.DeriveKey(passwordBytes, salt);

            // Verify password via HMAC check (constant-time, detects wrong password even on empty vault)
            if (!string.IsNullOrEmpty(vaultFile.PasswordCheck))
                VerifyPasswordCheck(key, vaultFile.PasswordCheck);

            // Restore persisted credentials
            if (vaultFile.EncryptedEntries is not null)
            {
                var blob = Convert.FromBase64String(vaultFile.EncryptedEntries);
                // AES-GCM auth tag will throw CryptographicException on wrong key — catches tampered files
                var json = VaultCrypto.DecryptDirect(blob, key);
                try
                {
                    var exported = JsonSerializer.Deserialize<List<VaultExportEntry>>(json)
                        ?? throw new InvalidDataException("Vault file entries are corrupt.");
                    foreach (var e in exported)
                        _entries[e.Id] = ToVaultEntry(e);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(json);
                }
            }

            lock (_lock)
            {
                if (_derivedKey is not null) CryptographicOperations.ZeroMemory(_derivedKey);
                _derivedKey = key;
                _masterSalt = salt;
            }

            _logger.LogInformation("Credential vault unlocked ({Count} credentials loaded)", _entries.Count);
            _lockStateSubject.OnNext(true);
            ResetIdleTimer();
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

            _masterSalt = null;
            _entries.Clear(); // Drop in-memory metadata on lock
        }

        _logger.LogInformation("Credential vault locked");
        _lockStateSubject.OnNext(false);
        return Task.CompletedTask;
    }

    public async Task<CredentialEntry> AddAsync(CredentialCreateRequest request, CancellationToken ct = default)
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
        await PersistAsync(ct);

        _logger.LogInformation("Credential {CredentialId} added (type={Type})", id, request.Type);
        return ToCredentialEntry(entry);
    }

    public async Task<CredentialEntry> UpdateAsync(Guid id, CredentialUpdateRequest request, CancellationToken ct = default)
    {
        EnsureUnlocked();
        ct.ThrowIfCancellationRequested();

        if (!_entries.TryGetValue(id, out var entry))
            throw new KeyNotFoundException($"Credential {id} not found.");

        if (request.Label is not null) entry.Label = request.Label;
        if (request.Username is not null) entry.Username = request.Username;

        if (request.Payload is not null)
            entry.EncryptedPayload = VaultCrypto.Encrypt(request.Payload, _derivedKey!);

        await PersistAsync(ct);

        _logger.LogInformation("Credential {CredentialId} updated", id);
        return ToCredentialEntry(entry);
    }

    public async Task RemoveAsync(Guid id, CancellationToken ct = default)
    {
        EnsureUnlocked();
        ct.ThrowIfCancellationRequested();

        if (!_entries.TryRemove(id, out var entry))
            throw new KeyNotFoundException($"Credential {id} not found.");

        CryptographicOperations.ZeroMemory(entry.EncryptedPayload);
        await PersistAsync(ct);

        _logger.LogInformation("Credential {CredentialId} removed", id);
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

    public async Task RotateEncryptionKeyAsync(SecureString newMasterPassword, CancellationToken ct = default)
    {
        EnsureUnlocked();
        ct.ThrowIfCancellationRequested();

        var newPasswordBytes = SecureStringToBytes(newMasterPassword);
        try
        {
            var newSalt = RandomNumberGenerator.GetBytes(32);
            var newKey = VaultCrypto.DeriveKey(newPasswordBytes, newSalt);

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
                _masterSalt = newSalt;
            }

            await PersistAsync(ct);

            _logger.LogInformation("Vault encryption key rotated, {Count} credentials re-encrypted", _entries.Count);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(newPasswordBytes);
        }
    }

    /// <summary>
    /// Export an encrypted, self-contained backup blob: [32-byte salt][encrypted entries].
    /// The blob can be decrypted with the master password that was active at export time.
    /// </summary>
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
        var encrypted = VaultCrypto.Encrypt(json, _derivedKey!);
        CryptographicOperations.ZeroMemory(json);

        // Prepend master salt so the blob is self-contained for cross-vault import
        var salt = _masterSalt!;
        var result = new byte[salt.Length + encrypted.Length];
        salt.CopyTo(result, 0);
        encrypted.AsSpan().CopyTo(result.AsSpan(salt.Length));

        _logger.LogInformation("Vault exported ({Count} credentials)", _entries.Count);
        return Task.FromResult(result);
    }

    /// <summary>
    /// Import credentials from a blob produced by <see cref="ExportAsync"/>.
    /// Each imported credential is re-encrypted with the current vault's key.
    /// </summary>
    public async Task ImportAsync(byte[] encryptedBlob, SecureString password, CancellationToken ct = default)
    {
        EnsureUnlocked();
        ct.ThrowIfCancellationRequested();

        if (encryptedBlob.Length < 32)
            throw new ArgumentException("Invalid export blob — too short.", nameof(encryptedBlob));

        var passwordBytes = SecureStringToBytes(password);
        try
        {
            // Extract the salt embedded by ExportAsync
            var exportSalt = encryptedBlob[..32];
            var exportBlob = encryptedBlob[32..];

            var importKey = VaultCrypto.DeriveKey(passwordBytes, exportSalt);
            byte[] decrypted;
            try
            {
                decrypted = VaultCrypto.Decrypt(exportBlob, importKey);
            }
            finally
            {
                // Do NOT zero importKey yet — needed to decrypt individual entry payloads below
            }

            var entries = JsonSerializer.Deserialize<List<VaultExportEntry>>(decrypted)
                ?? throw new InvalidOperationException("Invalid vault export format.");
            CryptographicOperations.ZeroMemory(decrypted);

            try
            {
                foreach (var imported in entries)
                {
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
            }
            finally
            {
                CryptographicOperations.ZeroMemory(importKey);
            }

            await PersistAsync(ct);

            _logger.LogInformation("Vault imported ({Count} credentials)", entries.Count);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordBytes);
        }
    }

    // ── Persistence ────────────────────────────────────────────────────────────

    /// <summary>
    /// Serialize and encrypt the current entry set to disk atomically.
    /// Uses AES-GCM direct (no extra KDF pass) — the Argon2id-derived key is
    /// already expensive to obtain; the AES authentication tag detects tampering.
    /// </summary>
    private async Task PersistAsync(CancellationToken ct = default)
    {
        byte[] key;
        byte[] salt;
        lock (_lock)
        {
            key = _derivedKey ?? throw new InvalidOperationException("Vault is not unlocked.");
            salt = _masterSalt ?? throw new InvalidOperationException("Vault salt is missing.");
        }

        await _persistLock.WaitAsync(ct);
        try
        {
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
            string encryptedB64;
            try
            {
                encryptedB64 = Convert.ToBase64String(VaultCrypto.EncryptDirect(json, key));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(json);
            }

            var vaultFile = new VaultFile
            {
                Version = 1,
                MasterSalt = Convert.ToBase64String(salt),
                PasswordCheck = ComputePasswordCheck(key),
                EncryptedEntries = encryptedB64,
            };

            await WriteVaultFileAtomicAsync(vaultFile, ct);
        }
        finally
        {
            _persistLock.Release();
        }
    }

    private async Task<VaultFile> ReadOrInitVaultFileAsync(CancellationToken ct)
    {
        if (File.Exists(_vaultFilePath))
        {
            var bytes = await File.ReadAllBytesAsync(_vaultFilePath, ct);
            return JsonSerializer.Deserialize<VaultFile>(bytes)
                ?? throw new InvalidDataException("Vault file is corrupt or empty.");
        }

        // First-time: generate a cryptographically random master salt (CRIT-3 fix)
        var newSalt = RandomNumberGenerator.GetBytes(32);
        var empty = new VaultFile
        {
            Version = 1,
            MasterSalt = Convert.ToBase64String(newSalt),
            PasswordCheck = string.Empty,
            EncryptedEntries = null,
        };
        await WriteVaultFileAtomicAsync(empty, ct);
        _logger.LogInformation("Vault initialized with new random master salt at {Path}", _vaultFilePath);
        return empty;
    }

    private async Task WriteVaultFileAtomicAsync(VaultFile vaultFile, CancellationToken ct)
    {
        var tempPath = _vaultFilePath + ".tmp";
        var json = JsonSerializer.SerializeToUtf8Bytes(vaultFile);
        await File.WriteAllBytesAsync(tempPath, json, ct);
        File.Move(tempPath, _vaultFilePath, overwrite: true);
    }

    private static string ComputePasswordCheck(byte[] key) =>
        Convert.ToBase64String(HMACSHA256.HashData(key, "HM.VAULT.V1.CHECK"u8));

    private static void VerifyPasswordCheck(byte[] key, string storedCheck)
    {
        var expected = HMACSHA256.HashData(key, "HM.VAULT.V1.CHECK"u8);
        if (!CryptographicOperations.FixedTimeEquals(expected, Convert.FromBase64String(storedCheck)))
            throw new CryptographicException("Incorrect master password.");
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

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
        _persistLock.Dispose();
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

    private static VaultEntry ToVaultEntry(VaultExportEntry e) => new()
    {
        Id = e.Id,
        Label = e.Label,
        Type = e.Type,
        Username = e.Username,
        EncryptedPayload = e.EncryptedPayload,
        AssociatedMachineIds = e.AssociatedMachineIds,
        CreatedUtc = e.CreatedUtc,
    };

    // ── Private types ──────────────────────────────────────────────────────────

    private sealed class VaultFile
    {
        public int Version { get; set; } = 1;
        /// <summary>Base64-encoded 32-byte random salt (CRIT-3 fix — unique per installation).</summary>
        public string MasterSalt { get; set; } = string.Empty;
        /// <summary>HMAC-SHA256 of fixed sentinel under the derived key — used to detect wrong password.</summary>
        public string PasswordCheck { get; set; } = string.Empty;
        /// <summary>Base64 of AES-GCM encrypted JSON entry array (null when vault is empty).</summary>
        public string? EncryptedEntries { get; set; }
    }

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

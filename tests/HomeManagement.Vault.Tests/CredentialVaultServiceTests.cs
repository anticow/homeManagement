using System.Reactive.Linq;
using System.Runtime.InteropServices;
using System.Security;
using FluentAssertions;
using HomeManagement.Abstractions;
using HomeManagement.Abstractions.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HomeManagement.Vault.Tests;

public sealed class CredentialVaultServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly CredentialVaultService _sut;

    public CredentialVaultServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "hm-vault-tests-" + Guid.NewGuid());
        Directory.CreateDirectory(_tempDir);
        _sut = CreateService(_tempDir);
    }

    private static CredentialVaultService CreateService(string dir) =>
        new(NullLogger<CredentialVaultService>.Instance,
            Options.Create(new VaultOptions { StoragePath = dir }));

    [Fact]
    public void IsUnlocked_InitiallyFalse()
    {
        _sut.IsUnlocked.Should().BeFalse();
    }

    [Fact]
    public async Task UnlockAsync_SetsIsUnlockedTrue()
    {
        await _sut.UnlockAsync(CreateSecureString("TestPassword123!"));

        _sut.IsUnlocked.Should().BeTrue();
    }

    [Fact]
    public async Task LockAsync_SetsIsUnlockedFalse()
    {
        await _sut.UnlockAsync(CreateSecureString("TestPassword123!"));
        await _sut.LockAsync();

        _sut.IsUnlocked.Should().BeFalse();
    }

    [Fact]
    public async Task LockStateChanged_EmitsOnUnlockAndLock()
    {
        var states = new List<bool>();
        using var sub = _sut.LockStateChanged.Subscribe(states.Add);

        await _sut.UnlockAsync(CreateSecureString("TestPassword123!"));
        await _sut.LockAsync();

        states.Should().Contain(true);
        states.Should().Contain(false);
    }

    [Fact]
    public async Task AddAsync_WhenLocked_ThrowsInvalidOperation()
    {
        var act = () => _sut.AddAsync(new CredentialCreateRequest(
            "label", CredentialType.Password, "user", [1, 2, 3]));

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task AddAsync_WhenUnlocked_ReturnsEntry()
    {
        await _sut.UnlockAsync(CreateSecureString("TestPassword123!"));

        var entry = await _sut.AddAsync(new CredentialCreateRequest(
            "Test SSH Key", CredentialType.SshKey, "admin", [1, 2, 3]));

        entry.Should().NotBeNull();
        entry.Label.Should().Be("Test SSH Key");
        entry.Type.Should().Be(CredentialType.SshKey);
    }

    [Fact]
    public async Task GetPayloadAsync_DecryptsCorrectly()
    {
        await _sut.UnlockAsync(CreateSecureString("TestPassword123!"));
        var payload = new byte[] { 72, 101, 108, 108, 111 }; // "Hello"

        var entry = await _sut.AddAsync(new CredentialCreateRequest(
            "Test Cred", CredentialType.Password, "user", payload));

        var retrieved = await _sut.GetPayloadAsync(entry.Id);

        retrieved.DecryptedPayload.ToArray().Should().BeEquivalentTo(payload);
        retrieved.Username.Should().Be("user");
    }

    [Fact]
    public async Task GetPayloadAsync_NotFound_ThrowsKeyNotFound()
    {
        await _sut.UnlockAsync(CreateSecureString("TestPassword123!"));

        var act = () => _sut.GetPayloadAsync(Guid.NewGuid());

        await act.Should().ThrowAsync<KeyNotFoundException>();
    }

    [Fact]
    public async Task UpdateAsync_UpdatesLabel()
    {
        await _sut.UnlockAsync(CreateSecureString("TestPassword123!"));
        var entry = await _sut.AddAsync(new CredentialCreateRequest(
            "Old", CredentialType.Password, "user", [1, 2]));

        var updated = await _sut.UpdateAsync(entry.Id, new CredentialUpdateRequest { Label = "New" });

        updated.Label.Should().Be("New");
    }

    [Fact]
    public async Task UpdateAsync_NotFound_ThrowsKeyNotFound()
    {
        await _sut.UnlockAsync(CreateSecureString("TestPassword123!"));

        var act = () => _sut.UpdateAsync(Guid.NewGuid(), new CredentialUpdateRequest { Label = "X" });

        await act.Should().ThrowAsync<KeyNotFoundException>();
    }

    [Fact]
    public async Task RemoveAsync_RemovesEntry()
    {
        await _sut.UnlockAsync(CreateSecureString("TestPassword123!"));
        var entry = await _sut.AddAsync(new CredentialCreateRequest(
            "ToDelete", CredentialType.Password, "user", [1]));

        await _sut.RemoveAsync(entry.Id);

        var act = () => _sut.GetPayloadAsync(entry.Id);
        await act.Should().ThrowAsync<KeyNotFoundException>();
    }

    [Fact]
    public async Task RemoveAsync_NotFound_ThrowsKeyNotFound()
    {
        await _sut.UnlockAsync(CreateSecureString("TestPassword123!"));

        var act = () => _sut.RemoveAsync(Guid.NewGuid());

        await act.Should().ThrowAsync<KeyNotFoundException>();
    }

    [Fact]
    public async Task ListAsync_ReturnsAllEntries()
    {
        await _sut.UnlockAsync(CreateSecureString("TestPassword123!"));
        await _sut.AddAsync(new CredentialCreateRequest("A", CredentialType.Password, "u1", [1]));
        await _sut.AddAsync(new CredentialCreateRequest("B", CredentialType.SshKey, "u2", [2]));

        var list = await _sut.ListAsync();

        list.Should().HaveCount(2);
    }

    [Fact]
    public async Task ListAsync_WhenLocked_Throws()
    {
        var act = () => _sut.ListAsync();
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    // ── MED-6: Persistence ─────────────────────────────────────────────────────

    [Fact]
    public async Task Persistence_CredentialsSurviveRestartWithSamePassword()
    {
        const string password = "PersistenceTest!99";
        var payload = new byte[] { 1, 2, 3, 4, 5 };

        // Add credential, then dispose
        await _sut.UnlockAsync(CreateSecureString(password));
        var added = await _sut.AddAsync(new CredentialCreateRequest(
            "Persisted", CredentialType.Password, "user", payload));
        await _sut.LockAsync();

        // New service instance pointing at same directory
        using var sut2 = CreateService(_tempDir);
        await sut2.UnlockAsync(CreateSecureString(password));

        var list = await sut2.ListAsync();
        list.Should().ContainSingle(e => e.Id == added.Id && e.Label == "Persisted");

        var retrieved = await sut2.GetPayloadAsync(added.Id);
        retrieved.DecryptedPayload.ToArray().Should().BeEquivalentTo(payload);
    }

    [Fact]
    public async Task Persistence_VaultFileIsCreatedOnUnlock()
    {
        await _sut.UnlockAsync(CreateSecureString("AnyPassword!1"));

        File.Exists(Path.Combine(_tempDir, "vault.dat")).Should().BeTrue();
    }

    [Fact]
    public async Task Persistence_EmptyVaultHasNoEntriesAfterRestart()
    {
        const string password = "EmptyRestart!1";
        await _sut.UnlockAsync(CreateSecureString(password));
        await _sut.LockAsync();

        using var sut2 = CreateService(_tempDir);
        await sut2.UnlockAsync(CreateSecureString(password));

        var list = await sut2.ListAsync();
        list.Should().BeEmpty();
    }

    // ── CRIT-3: Random salt ────────────────────────────────────────────────────

    [Fact]
    public async Task MasterSalt_IsDifferentAcrossNewVaultInstances()
    {
        var dir2 = _tempDir + "_b";
        Directory.CreateDirectory(dir2);
        try
        {
            using var sut2 = CreateService(dir2);

            await _sut.UnlockAsync(CreateSecureString("SamePassword!1"));
            await sut2.UnlockAsync(CreateSecureString("SamePassword!1"));

            // The vault files must exist and contain different salts
            var file1 = await File.ReadAllTextAsync(Path.Combine(_tempDir, "vault.dat"));
            var file2 = await File.ReadAllTextAsync(Path.Combine(dir2, "vault.dat"));

            // If salts were deterministic, files of two empty vaults with the same password
            // would be identical. With random salts they must differ.
            file1.Should().NotBe(file2);
        }
        finally
        {
            Directory.Delete(dir2, recursive: true);
        }
    }

    [Fact]
    public async Task WrongPassword_ThrowsCryptographicException_AfterFirstPersist()
    {
        const string rightPassword = "RightPassword!9";
        await _sut.UnlockAsync(CreateSecureString(rightPassword));
        await _sut.AddAsync(new CredentialCreateRequest("cred", CredentialType.Password, "u", [42]));
        await _sut.LockAsync();

        using var sut2 = CreateService(_tempDir);
        var act = () => sut2.UnlockAsync(CreateSecureString("WrongPassword!9"));

        await act.Should().ThrowAsync<System.Security.Cryptography.CryptographicException>();
    }

    private static SecureString CreateSecureString(string value)
    {
        var ss = new SecureString();
        foreach (var c in value)
            ss.AppendChar(c);
        ss.MakeReadOnly();
        return ss;
    }

    public void Dispose()
    {
        _sut.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort cleanup */ }
    }
}

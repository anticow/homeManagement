using System.Reactive.Linq;
using System.Runtime.InteropServices;
using System.Security;
using FluentAssertions;
using HomeManagement.Abstractions;
using HomeManagement.Abstractions.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace HomeManagement.Vault.Tests;

public sealed class CredentialVaultServiceTests : IDisposable
{
    private readonly CredentialVaultService _sut;

    public CredentialVaultServiceTests()
    {
        _sut = new CredentialVaultService(NullLogger<CredentialVaultService>.Instance);
    }

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

    private static SecureString CreateSecureString(string value)
    {
        var ss = new SecureString();
        foreach (var c in value)
            ss.AppendChar(c);
        ss.MakeReadOnly();
        return ss;
    }

    public void Dispose() => _sut.Dispose();
}

using System.Security.Cryptography;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using HomeManagement.Abstractions.Models;

namespace HomeManagement.AgentGateway.Host.Services;

// The lock inode is separate from the state file, which is replaced on every commit.
internal sealed class EncryptedAgentEnrollmentStore : IDisposable
{
    internal const int MaximumStateBytes = 4 * 1024 * 1024;
    private const int FormatVersion = 1;
    private readonly string _directory;
    private readonly FileStream _lock;
    private readonly byte[] _key;
    private readonly Action<EnrollmentCommitStage>? _commitHook;
    private bool _disposed;

    public EncryptedAgentEnrollmentStore(string directory, string wrappingKeyPath,
        Action<EnrollmentCommitStage>? commitHook = null)
    {
        _directory = Path.GetFullPath(directory);
        _key = ReadKey(wrappingKeyPath);
        try
        {
            _lock = OpenLock(_directory);
        }
        catch
        {
            CryptographicOperations.ZeroMemory(_key);
            throw;
        }
        _commitHook = commitHook;
    }

    public AgentEnrollmentDocument Load()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var marker = ReadBounded(Path.Combine(_directory, "initialized.json"));
        var registryId = JsonSerializer.Deserialize<Guid>(marker);
        var state = Decode(ReadBounded(Path.Combine(_directory, "registry.json")), _key);
        if (registryId == Guid.Empty || registryId != state.RegistryId)
            throw new InvalidDataException("Agent enrollment initialization marker does not match.");
        AgentEnrollmentRegistry.Validate(state);
        return state;
    }

    internal bool UsesWrappingKey(string value) =>
        AgentEnrollmentRegistry.IsStrongKey(value)
        && CryptographicOperations.FixedTimeEquals(_key, Convert.FromBase64String(value));

    public void Commit(AgentEnrollmentDocument document)
        => Write(document, initialize: false);

    private void Write(AgentEnrollmentDocument document, bool initialize)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        AgentEnrollmentRegistry.Validate(document);
        if (!initialize)
        {
            var previous = Load();
            if (previous.RegistryId != document.RegistryId || document.Sequence != checked(previous.Sequence + 1))
                throw new InvalidDataException("Agent enrollment commit sequence conflict.");
        }
        var encoded = Encode(document, _key);
        var temporary = Path.Combine(_directory, $"registry-{Guid.NewGuid():N}.tmp");
        try
        {
            _commitHook?.Invoke(EnrollmentCommitStage.BeforeWrite);
            using (var stream = CreatePrivate(temporary))
            {
                stream.Write(encoded);
                stream.Flush(flushToDisk: true);
            }
            _commitHook?.Invoke(EnrollmentCommitStage.BeforeReplace);
            File.Move(temporary, Path.Combine(_directory, "registry.json"), overwrite: true);
            _commitHook?.Invoke(EnrollmentCommitStage.AfterReplace);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    // Explicit offline operation only. Normal startup never creates an empty registry.
    public static void Initialize(string directory, string wrappingKeyPath,
        IReadOnlyList<AgentEnrollmentAssignment> assignments)
    {
        var fullPath = Path.GetFullPath(directory);
        if (!Directory.Exists(fullPath) || Directory.EnumerateFileSystemEntries(fullPath).Any())
            throw new InvalidOperationException("Initialization requires an existing empty dedicated directory.");
        if (OperatingSystem.IsWindows())
        {
            using var identity = WindowsIdentity.GetCurrent();
            var user = identity.User ?? throw new InvalidOperationException("The initialization account has no security identity.");
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            foreach (var owner in new[] { user, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null) })
                security.AddAccessRule(new FileSystemAccessRule(owner, FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(fullPath).SetAccessControl(security);
        }
        else
            File.SetUnixFileMode(fullPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        using var store = new EncryptedAgentEnrollmentStore(fullPath, wrappingKeyPath);
        // Recheck under the stable lock; never initialize over an existing store.
        if (Directory.EnumerateFileSystemEntries(fullPath).Any(path => Path.GetFileName(path) != "registry.lock"))
            throw new InvalidOperationException("The enrollment directory is no longer empty.");
        var document = AgentEnrollmentRegistry.CreateDocument(assignments);
        store.Write(document, initialize: true);
        using var marker = CreatePrivate(Path.Combine(fullPath, "initialized.json"));
        marker.Write(JsonSerializer.SerializeToUtf8Bytes(document.RegistryId));
        marker.Flush(flushToDisk: true);
    }

    private static FileStream OpenLock(string directory)
    {
        if (!Directory.Exists(directory))
            throw new DirectoryNotFoundException("The agent enrollment directory must already exist.");
        return new FileStream(Path.Combine(directory, "registry.lock"),
            PrivateOptions(FileMode.OpenOrCreate));
    }

    private static FileStream CreatePrivate(string path) => new(path, PrivateOptions(FileMode.CreateNew));

    private static FileStreamOptions PrivateOptions(FileMode mode)
    {
        var options = new FileStreamOptions { Mode = mode, Access = FileAccess.ReadWrite, Share = FileShare.None };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        return options;
    }

    private static byte[] ReadKey(string path)
    {
        var encoded = ReadBounded(path);
        try
        {
            var value = Encoding.UTF8.GetString(encoded).TrimEnd('\r', '\n');
            if (!AgentEnrollmentRegistry.IsStrongKey(value))
                throw new InvalidDataException("The enrollment wrapping key must be canonical Base64 with 32 random bytes.");
            return Convert.FromBase64String(value);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encoded);
        }
    }

    private static byte[] ReadBounded(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length is < 1 or > MaximumStateBytes)
            throw new InvalidDataException("Agent enrollment file size is invalid.");
        var result = new byte[checked((int)stream.Length)];
        stream.ReadExactly(result);
        return result;
    }

    private static byte[] Encode(AgentEnrollmentDocument document, byte[] key)
    {
        var plain = JsonSerializer.SerializeToUtf8Bytes(document);
        try
        {
            if (plain.Length > MaximumStateBytes / 2)
                throw new InvalidDataException("Agent enrollment state exceeds the size limit.");
            var nonce = RandomNumberGenerator.GetBytes(12);
            var cipher = new byte[plain.Length];
            var tag = new byte[16];
            using var aes = new AesGcm(key, 16);
            aes.Encrypt(nonce, plain, cipher, tag, Aad(document.RegistryId, document.Sequence));
            return JsonSerializer.SerializeToUtf8Bytes(
                new Envelope(FormatVersion, document.RegistryId, document.Sequence, nonce, tag, cipher));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    private static AgentEnrollmentDocument Decode(byte[] encoded, byte[] key)
    {
        var envelope = JsonSerializer.Deserialize<Envelope>(encoded)
            ?? throw new InvalidDataException("Missing agent enrollment envelope.");
        if (envelope.Format != FormatVersion || envelope.RegistryId == Guid.Empty || envelope.Sequence < 1
            || envelope.Nonce is not { Length: 12 } || envelope.Tag is not { Length: 16 }
            || envelope.Ciphertext is not { Length: > 0 })
            throw new InvalidDataException("Invalid agent enrollment envelope.");
        var plain = new byte[envelope.Ciphertext.Length];
        try
        {
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(envelope.Nonce, envelope.Ciphertext, envelope.Tag, plain,
                Aad(envelope.RegistryId, envelope.Sequence));
            var document = JsonSerializer.Deserialize<AgentEnrollmentDocument>(plain)
                ?? throw new InvalidDataException("Missing agent enrollment state.");
            if (document.RegistryId != envelope.RegistryId || document.Sequence != envelope.Sequence)
                throw new InvalidDataException("Agent enrollment envelope does not match state.");
            return document;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    private static byte[] Aad(Guid id, long sequence) =>
        Encoding.UTF8.GetBytes(FormattableString.Invariant($"hm-agent-enrollment:{FormatVersion}:{id:D}:{sequence}"));

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lock.Dispose();
        CryptographicOperations.ZeroMemory(_key);
    }

    private sealed record Envelope(int Format, Guid RegistryId, long Sequence,
        byte[] Nonce, byte[] Tag, byte[] Ciphertext);
}

internal enum EnrollmentCommitStage { BeforeWrite, BeforeReplace, AfterReplace }

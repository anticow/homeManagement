using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace HomeManagement.AgentGateway.Host.Services;

/// <summary>
/// In-memory revocation store with optional file-based persistence so the
/// blocklist survives pod restarts. The backing file is written atomically
/// (write temp → rename) to prevent corruption on crash.
/// </summary>
public sealed class RevokedAgentStore : IRevokedAgentStore
{
    private readonly ConcurrentDictionary<string, RevokedAgentEntry> _revoked =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly string? _persistencePath;
    private readonly ILogger<RevokedAgentStore> _logger;

    private static readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };

    public RevokedAgentStore(IConfiguration configuration, ILogger<RevokedAgentStore> logger)
    {
        _logger = logger;
        _persistencePath = configuration["AgentGateway:RevocationListPath"];

        if (!string.IsNullOrWhiteSpace(_persistencePath))
        {
            LoadFromDisk();
        }
    }

    public void Revoke(string agentId, string reason)
    {
        var entry = new RevokedAgentEntry(agentId, reason, DateTimeOffset.UtcNow);
        _revoked[agentId] = entry;
        _logger.LogWarning("Agent {AgentId} revoked: {Reason}", agentId, reason);
        PersistToDisk();
    }

    public void Reinstate(string agentId)
    {
        if (_revoked.TryRemove(agentId, out _))
        {
            _logger.LogInformation("Agent {AgentId} reinstated — revocation removed", agentId);
            PersistToDisk();
        }
    }

    public bool IsRevoked(string agentId) =>
        _revoked.ContainsKey(agentId);

    public IReadOnlyList<RevokedAgentEntry> GetAll() =>
        [.. _revoked.Values.OrderBy(e => e.RevokedAt)];

    private void LoadFromDisk()
    {
        try
        {
            if (!File.Exists(_persistencePath))
                return;

            var json = File.ReadAllText(_persistencePath);
            var entries = JsonSerializer.Deserialize<List<RevokedAgentEntry>>(json) ?? [];

            foreach (var entry in entries)
                _revoked[entry.AgentId] = entry;

            _logger.LogInformation("Loaded {Count} revoked agent(s) from {Path}",
                _revoked.Count, _persistencePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load revocation list from {Path} — starting empty", _persistencePath);
        }
    }

    private void PersistToDisk()
    {
        if (string.IsNullOrWhiteSpace(_persistencePath))
            return;

        try
        {
            var dir = Path.GetDirectoryName(_persistencePath);
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);

            var tmp = _persistencePath + ".tmp";
            var json = JsonSerializer.Serialize(GetAll(), _jsonOptions);
            File.WriteAllText(tmp, json);
            File.Move(tmp, _persistencePath, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist revocation list to {Path}", _persistencePath);
        }
    }
}

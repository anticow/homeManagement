using System.Text.Json;
using System.Text.RegularExpressions;
using HomeManagement.Abstractions.Models;
using HomeManagement.Agent.Configuration;
using Microsoft.Extensions.Options;

namespace HomeManagement.Agent.Handlers;

public sealed partial class ManagedServiceRegistry(IOptions<AgentConfiguration> options)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow
    };

    // Reload on every operation: AWX can withdraw enrollment without restarting the agent.
    public IReadOnlyList<ManagedServiceEnrollment> Read()
    {
        var config = options.Value;
        if (string.IsNullOrWhiteSpace(config.ManagedServicesPath))
            return [];
        if (!config.UseTls || !config.ManagedServicesOnly)
            throw new InvalidOperationException("Managed services require encrypted, restricted agent control.");
        if (File.Exists(config.ManagedServicesPath + ".disabled"))
            throw new InvalidOperationException("Remote management is disabled locally.");

        var manifest = JsonSerializer.Deserialize<ManagedServiceManifest>(
            File.ReadAllText(config.ManagedServicesPath), JsonOptions)
            ?? throw new InvalidOperationException("Managed service manifest is empty.");
        if (manifest.SchemaVersion != 1 || manifest.Services is null || manifest.Services.Count > 32)
            throw new InvalidOperationException("Unsupported managed service manifest.");

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var service in manifest.Services)
        {
            if (service is null || string.IsNullOrWhiteSpace(service.Id)
                || !SafeName().IsMatch(service.Id) || !ids.Add(service.Id)
                || string.IsNullOrWhiteSpace(service.ServiceName)
                || !SafeName().IsMatch(service.ServiceName) || !names.Add(service.ServiceName)
                || string.IsNullOrWhiteSpace(service.DisplayName) || service.DisplayName.Length > 128)
                throw new InvalidOperationException("Invalid or duplicate managed service enrollment.");
        }
        return manifest.Services;
    }

    [GeneratedRegex(@"^[a-zA-Z0-9][a-zA-Z0-9_.-]{0,79}$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeName();
}

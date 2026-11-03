using HomeManagement.Abstractions.Models;
using HomeManagement.Abstractions.Validation;

namespace HomeManagement.Transport;

public sealed partial class RemoteAgentGatewayClient
{
    public async Task<bool> VerifyEnrollmentModeAsync(CancellationToken ct = default)
    {
        using var budget = EnrollmentBudget(ct);
        if (_options.EnrollmentEnabled) RequireEncryptedControlPlane();
        var capabilities = await GetCapabilitiesAsync(budget.Token);
        if (capabilities.EnrollmentEnabled != _options.EnrollmentEnabled)
            throw new InvalidOperationException("Gateway enrollment mode changed or mismatches this process. No runtime fallback is permitted.");
        return capabilities.EnrollmentEnabled;
    }

    private async Task RequireLegacyRuntimeAsync(CancellationToken ct)
    {
        if (await VerifyEnrollmentModeAsync(ct))
            throw new InvalidOperationException("Managed inventory runtime requires an explicit machine GUID, not an agent ID or hostname.");
    }

    public async Task<RemoteResult> SendMachineCommandAsync(MachineTarget target, RemoteCommand command, CancellationToken ct = default)
    {
        var timeoutSeconds = AgentRuntimeRequestValidation.ValidateCommand(command);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds + 30));
        if (!await VerifyEnrollmentModeAsync(budget.Token))
            return await SendCommandAsync(ResolveLegacyAgentId(target,
                command.CommandType.Equals("ManagedServices", StringComparison.OrdinalIgnoreCase)), command, budget.Token);
        using var response = await SendEnrollmentAsync(HttpMethod.Post, MachinePath(target.MachineId, "commands"), command, budget.Token);
        return await ReadEnrollmentResponseAsync<RemoteResult>(response, budget.Token, 4 * 1024 * 1024);
    }

    public async Task<AgentMetadata> GetMachineMetadataAsync(MachineTarget target, CancellationToken ct = default)
    {
        using var budget = EnrollmentBudget(ct);
        if (!await VerifyEnrollmentModeAsync(budget.Token))
            return await GetMetadataAsync(ResolveLegacyAgentId(target), budget.Token);
        using var response = await SendEnrollmentAsync(HttpMethod.Get, MachinePath(target.MachineId, "metadata"), null, budget.Token);
        return await ReadEnrollmentResponseAsync<AgentMetadata>(response, budget.Token);
    }

    public async Task RequestMachineUpdateAsync(MachineTarget target, AgentUpdatePackage package, CancellationToken ct = default)
    {
        AgentRuntimeRequestValidation.ValidateUpdate(package);
        using var budget = EnrollmentBudget(ct);
        if (!await VerifyEnrollmentModeAsync(budget.Token))
        {
            await RequestUpdateAsync(ResolveLegacyAgentId(target), package, budget.Token);
            return;
        }
        using var response = await SendEnrollmentAsync(HttpMethod.Post, MachinePath(target.MachineId, "updates"), package, budget.Token);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException("Bound machine update was rejected; no retry was attempted.", null, response.StatusCode);
    }

    public async Task<MachineAgentObservation> GetMachineObservationAsync(Guid machineId, CancellationToken ct = default)
    {
        using var budget = EnrollmentBudget(ct);
        if (!await VerifyEnrollmentModeAsync(budget.Token))
            throw new InvalidOperationException("Live bound-machine observations require managed enrollment.");
        using var response = await SendEnrollmentAsync(HttpMethod.Get, MachinePath(machineId, "observation"), null, budget.Token);
        var observation = await ReadEnrollmentResponseAsync<MachineAgentObservation>(response, budget.Token);
        if (observation.MachineId != machineId || observation.RegistryId == Guid.Empty
            || observation.BindingVersion < 1 || observation.CredentialGeneration < 1
            || string.IsNullOrWhiteSpace(observation.AgentId) || observation.Connected != observation.SessionId.HasValue)
            throw new InvalidOperationException("Gateway returned an invalid bound-machine observation.");
        return observation;
    }

    private string ResolveLegacyAgentId(MachineTarget target, bool managedServices = false)
    {
        if (managedServices) return target.Hostname.Value;
        return AgentTransportProvider.ResolveLegacyAgentId(this, target);
    }

    private static string MachinePath(Guid machineId, string operation)
    {
        if (machineId == Guid.Empty) throw new ArgumentException("An explicit machine GUID is required.");
        return $"/internal/agents/machines/{machineId:D}/{operation}";
    }
}

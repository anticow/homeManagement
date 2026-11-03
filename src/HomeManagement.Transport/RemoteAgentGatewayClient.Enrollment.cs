using System.Net.Http.Json;
using System.Net;
using System.Text.Json;
using HomeManagement.Abstractions.Models;

namespace HomeManagement.Transport;

public sealed partial class RemoteAgentGatewayClient
{
    private static readonly JsonSerializerOptions EnrollmentJsonOptions = new(JsonSerializerDefaults.Web);
    public async Task<AgentEnrollmentMetadata> GetEnrollmentAsync(Guid machineId, CancellationToken ct = default)
    {
        using var budget = EnrollmentBudget(ct);
        await RequireEnrollmentModeAsync(budget.Token);
        return await GetBindingAsync(machineId, budget.Token);
    }

    public async Task<AgentEnrollmentMetadata> AssignEnrollmentAsync(Guid machineId,
        MachineAgentEnrollmentAssignment assignment, CancellationToken ct = default)
    {
        using var budget = EnrollmentBudget(ct);
        await RequireEnrollmentModeAsync(budget.Token);
        using var response = await SendEnrollmentAsync(HttpMethod.Post, "/internal/agents/enrollments",
            new AgentEnrollmentAssignment(assignment.AgentId, assignment.InventoryHostname, assignment.ExpectedWireHostname, machineId),
            budget.Token);
        return RequireMachine(await ReadEnrollmentResponseAsync<AgentEnrollmentMetadata>(response, budget.Token), machineId);
    }

    public async Task<AgentEnrollmentMetadata> ChangeEnrollmentAsync(Guid machineId,
        AgentEnrollmentChange change, CancellationToken ct = default)
    {
        using var budget = EnrollmentBudget(ct);
        await RequireEnrollmentModeAsync(budget.Token);
        var binding = await GetBindingAsync(machineId, budget.Token);
        RequireConfirmedVersion(binding, change.ExpectedVersion);
        using var response = await SendEnrollmentAsync(HttpMethod.Post,
            $"/internal/agents/enrollments/{Uri.EscapeDataString(binding.AgentId)}/transition",
            change, budget.Token);
        return RequireMachine(await ReadEnrollmentResponseAsync<AgentEnrollmentMetadata>(response, budget.Token), machineId);
    }

    public async Task<AgentEnrollmentMetadata> RebindEnrollmentAsync(Guid machineId,
        AgentEnrollmentRebind rebind, CancellationToken ct = default)
    {
        using var budget = EnrollmentBudget(ct);
        await RequireEnrollmentModeAsync(budget.Token);
        var binding = await GetBindingAsync(machineId, budget.Token);
        RequireConfirmedVersion(binding, rebind.ExpectedVersion);
        using var response = await SendEnrollmentAsync(HttpMethod.Post,
            $"/internal/agents/enrollments/{Uri.EscapeDataString(binding.AgentId)}/rebind",
            new { rebind.ExpectedVersion, rebind.InventoryHostname, rebind.ExpectedWireHostname, MachineId = machineId },
            budget.Token);
        return RequireMachine(await ReadEnrollmentResponseAsync<AgentEnrollmentMetadata>(response, budget.Token), machineId);
    }

    private async Task<AgentEnrollmentMetadata> GetBindingAsync(Guid machineId, CancellationToken ct)
    {
        using var response = await SendEnrollmentAsync(HttpMethod.Get, $"/internal/agents/enrollments/machines/{machineId:D}", null, ct);
        return RequireMachine(await ReadEnrollmentResponseAsync<AgentEnrollmentMetadata>(response, ct), machineId);
    }

    private static AgentEnrollmentMetadata RequireMachine(AgentEnrollmentMetadata metadata, Guid machineId)
    {
        if (machineId == Guid.Empty || metadata.MachineId != machineId || string.IsNullOrWhiteSpace(metadata.AgentId)
            || metadata.Version < 1 || metadata.CredentialGeneration < 1 || !Enum.IsDefined(metadata.State))
            throw new InvalidOperationException("Gateway enrollment binding is invalid. No hostname fallback is permitted.");
        return metadata;
    }

    private static void RequireConfirmedVersion(AgentEnrollmentMetadata binding, long expectedVersion)
    {
        if (binding.Version != expectedVersion)
            throw new HttpRequestException("Enrollment changed; refresh metadata and confirm again.", null, HttpStatusCode.Conflict);
    }

    private async Task RequireEnrollmentModeAsync(CancellationToken ct)
    {
        RequireEncryptedControlPlane();
        if (!await VerifyEnrollmentModeAsync(ct))
            throw new InvalidOperationException("Managed enrollment is disabled in this client.");
    }

    private async Task<AgentEnrollmentCapabilities> GetCapabilitiesAsync(CancellationToken ct)
    {
        using var response = await SendEnrollmentAsync(HttpMethod.Get, "/internal/agents/capabilities", null, ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException("Gateway capabilities could not be verified. No legacy fallback is permitted.");
        return await ReadEnrollmentResponseAsync<AgentEnrollmentCapabilities>(response, ct);
    }

    private static async Task<T> ReadEnrollmentResponseAsync<T>(HttpResponseMessage response, CancellationToken ct, int limit = 16 * 1024)
    {
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException("Gateway enrollment operation was rejected; refresh metadata before retrying a mutation.",
                null, response.StatusCode);
        try
        {
            if (response.Content.Headers.ContentLength > limit)
                throw new InvalidOperationException("Gateway enrollment metadata exceeded its size limit.");
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            var bytes = new byte[limit + 1];
            var length = 0;
            while (length < bytes.Length)
            {
                var read = await stream.ReadAsync(bytes.AsMemory(length), ct);
                if (read == 0) break;
                length += read;
            }
            if (length > limit)
                throw new InvalidOperationException("Gateway enrollment metadata exceeded its size limit.");
            return JsonSerializer.Deserialize<T>(bytes.AsSpan(0, length), EnrollmentJsonOptions)
                ?? throw new InvalidOperationException("Gateway enrollment returned no metadata.");
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("Gateway enrollment returned invalid metadata.");
        }
    }

    private async Task<HttpResponseMessage> SendEnrollmentAsync(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = JsonContent.Create(body);
        return await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
    }

    private static CancellationTokenSource EnrollmentBudget(CancellationToken ct)
    {
        var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(30));
        return budget;
    }
}

using HomeManagement.Abstractions.Models;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using HomeManagement.AI.Abstractions.Contracts;
using Microsoft.Extensions.Logging;

namespace HomeManagement.Automation;

internal sealed class WorkflowPlanner : IWorkflowPlanner
{
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        WriteIndented = false,
    };

    private const string SystemPrompt =
        "You are an SRE automation assistant. " +
        "Given an objective, produce a structured workflow plan as JSON. " +
        "You MUST return ONLY valid JSON — no prose, no markdown fences. " +
        "Allowed step kinds: GatherMetrics, ListServices, RestartService, ApplyPatch, ShutdownMachine, RunScript. " +
        "Schema: { \"steps\": [ { \"name\": string, \"kind\": string, \"description\": string, \"parameters\": { key: value } } ] }";

    private readonly ILLMClient _llmClient;
    private readonly ILogger<WorkflowPlanner> _logger;

    public WorkflowPlanner(ILLMClient llmClient, ILogger<WorkflowPlanner> logger)
    {
        _llmClient = llmClient;
        _logger = logger;
    }

    public async Task<WorkflowPlan> CreatePlanAsync(CreatePlanRequest request, CancellationToken ct = default)
    {
        var generationRequest = new LLMGenerationRequest(
            Prompt: $"Objective: {request.Objective}",
            SystemPrompt: SystemPrompt,
            MaxTokens: 512,
            Temperature: 0.1);

        LLMGenerationResult result;

        try
        {
            result = await _llmClient.GenerateAsync(generationRequest, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LLM call failed during plan generation for objective: {Objective}", request.Objective);
            return BuildFallbackPlan(request.Objective, $"LLM unavailable: {ex.Message}");
        }

        if (!result.Success || string.IsNullOrWhiteSpace(result.Content))
        {
            return BuildFallbackPlan(request.Objective, result.Error ?? "LLM returned empty content.");
        }

        return ParsePlan(request.Objective, result.Content);
    }

    private WorkflowPlan ParsePlan(string objective, string llmContent)
    {
        try
        {
            var dto = JsonSerializer.Deserialize<PlanDto>(llmContent.Trim(), _jsonOptions)
                ?? throw new JsonException("Deserialised to null.");

            var steps = (dto.Steps ?? []).Select((s, index) =>
            {
                var stepName = string.IsNullOrWhiteSpace(s.Name) ? $"step-{index + 1}" : s.Name.Trim();
                var parameters = ValidateAndNormalizeParameters(stepName, s.Parameters);

                return new PlanStep(
                    Name: stepName,
                    Kind: ParseKind(s.Kind),
                    Description: s.Description ?? string.Empty,
                    Parameters: parameters);
            }).ToList();

            var hash = ComputeHash(steps);
            return new WorkflowPlan(
                Id: WorkflowPlanId.New(),
                Objective: objective,
                Steps: steps,
                RiskLevel: PlanRiskLevel.Low,  // policy engine will override this
                PlanHash: hash,
                Status: PlanStatus.PendingApproval,
                CreatedUtc: DateTime.UtcNow,
                ApprovedUtc: null,
                RejectionReason: null);
        }
        catch (PlanSchemaValidationException)
        {
            // Let strict schema violations bubble up so the engine can hard-reject
            // before persistence.
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse LLM plan JSON for objective: {Objective}", objective);
            return BuildFallbackPlan(objective, $"Could not parse LLM response as a valid plan: {ex.Message}");
        }
    }

    private static WorkflowPlan BuildFallbackPlan(string objective, string reason)
    {
        // An empty plan — policy will allow it (no denied steps) but it signals a planning failure.
        return new WorkflowPlan(
            Id: WorkflowPlanId.New(),
            Objective: objective,
            Steps: [],
            RiskLevel: PlanRiskLevel.Low,
            PlanHash: ComputeHash([]),
            Status: PlanStatus.PendingApproval,
            CreatedUtc: DateTime.UtcNow,
            ApprovedUtc: null,
            RejectionReason: reason);
    }

    internal static string ComputeHash(IReadOnlyList<PlanStep> steps)
    {
        // Canonical form: array of objects sorted by step name, parameters sorted by key.
        var canonical = steps
            .OrderBy(s => s.Name, StringComparer.Ordinal)
            .Select(s => new
            {
                name = s.Name,
                kind = s.Kind.ToString(),
                description = s.Description,
                parameters = s.Parameters
                    .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                    .ToDictionary(kv => kv.Key, kv => kv.Value)
            });

        var json = JsonSerializer.Serialize(canonical, _jsonOptions);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static PlanStepKind ParseKind(string? raw) =>
        Enum.TryParse<PlanStepKind>(raw, ignoreCase: true, out var parsed) ? parsed : PlanStepKind.Unknown;

    private static Dictionary<string, string> ValidateAndNormalizeParameters(
        string stepName,
        Dictionary<string, JsonElement>? parameters)
    {
        if (parameters is null)
        {
            return new Dictionary<string, string>();
        }

        var normalized = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in parameters)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                throw new PlanSchemaValidationException(
                    $"Planner output is invalid: step '{stepName}' contains an empty parameter name.");
            }

            // Strict contract: planner parameters must be scalar string values only.
            // This blocks nested object/array payloads from being persisted.
            if (value.ValueKind != JsonValueKind.String)
            {
                var kind = value.ValueKind.ToString();
                throw new PlanSchemaValidationException(
                    $"Planner output is invalid: parameter '{key}' on step '{stepName}' must be a string, but was {kind}.");
            }

            normalized[key] = value.GetString() ?? string.Empty;
        }

        return normalized;
    }

    // ── DTO shapes (internal, not exposed as public API) ─────────────────────

    private sealed class PlanDto
    {
        [JsonPropertyName("steps")]
        public List<PlanStepDto>? Steps { get; set; }
    }

    private sealed class PlanStepDto
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("kind")]
        public string? Kind { get; set; }

        [JsonPropertyName("description")]
        public string? Description { get; set; }

        [JsonPropertyName("parameters")]
        public Dictionary<string, JsonElement>? Parameters { get; set; }
    }
}


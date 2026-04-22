namespace HomeManagement.AI.Abstractions.Contracts;

public sealed record LLMGenerationRequest(
    string Prompt,
    string? SystemPrompt = null,
    string? ModelOverride = null,
    int? MaxTokens = null,
    double? Temperature = null,
    string? ResponseSchema = null);

public sealed record LLMGenerationResult(
    bool Success,
    string Model,
    string Content,
    int? PromptTokens,
    int? CompletionTokens,
    TimeSpan Latency,
    string? Error = null);

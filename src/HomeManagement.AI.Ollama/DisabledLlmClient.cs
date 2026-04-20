using HomeManagement.AI.Abstractions.Contracts;

namespace HomeManagement.AI.Ollama;

internal sealed class DisabledLlmClient : ILLMClient
{
    public Task<LLMGenerationResult> GenerateAsync(LLMGenerationRequest request, CancellationToken ct = default)
    {
        return Task.FromResult(new LLMGenerationResult(
            Success: false,
            Model: "disabled",
            Content: string.Empty,
            PromptTokens: null,
            CompletionTokens: null,
            Latency: TimeSpan.Zero,
            Error: "AI provider is disabled by configuration."));
    }
}

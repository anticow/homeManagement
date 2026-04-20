namespace HomeManagement.AI.Abstractions.Contracts;

public interface ILLMClient
{
    Task<LLMGenerationResult> GenerateAsync(LLMGenerationRequest request, CancellationToken ct = default);
}

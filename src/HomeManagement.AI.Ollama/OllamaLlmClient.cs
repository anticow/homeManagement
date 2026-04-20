using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using HomeManagement.AI.Abstractions.Configuration;
using HomeManagement.AI.Abstractions.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HomeManagement.AI.Ollama;

internal sealed class OllamaLlmClient : ILLMClient
{
    private readonly HttpClient _httpClient;
    private readonly IOptionsMonitor<AiOptions> _options;
    private readonly ILogger<OllamaLlmClient> _logger;

    public OllamaLlmClient(
        HttpClient httpClient,
        IOptionsMonitor<AiOptions> options,
        ILogger<OllamaLlmClient> logger)
    {
        _httpClient = httpClient;
        _options = options;
        _logger = logger;
    }

    public async Task<LLMGenerationResult> GenerateAsync(LLMGenerationRequest request, CancellationToken ct = default)
    {
        var ai = _options.CurrentValue;
        var ollama = ai.Ollama;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var payload = new OllamaGenerateRequest(
            Model: string.IsNullOrWhiteSpace(request.ModelOverride) ? ollama.Model : request.ModelOverride,
            Prompt: request.Prompt,
            System: request.SystemPrompt,
            Stream: false,
            Options: new OllamaOptionsPayload(
                NumCtx: ollama.NumCtx,
                Temperature: request.Temperature ?? ollama.Temperature,
                NumPredict: request.MaxTokens ?? ollama.MaxTokens));

        try
        {
            using var response = await _httpClient.PostAsJsonAsync("api/generate", payload, ct);
            response.EnsureSuccessStatusCode();

            var parsed = await response.Content.ReadFromJsonAsync<OllamaGenerateResponse>(cancellationToken: ct);
            if (parsed is null)
            {
                return Failed("Empty response payload from Ollama.", sw.Elapsed);
            }

            return new LLMGenerationResult(
                Success: true,
                Model: parsed.Model,
                Content: parsed.Response ?? string.Empty,
                PromptTokens: parsed.PromptEvalCount,
                CompletionTokens: parsed.EvalCount,
                Latency: sw.Elapsed,
                Error: null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Ollama generate request failed");
            return Failed(ex.Message, sw.Elapsed);
        }
    }

    private static LLMGenerationResult Failed(string error, TimeSpan latency)
        => new(
            Success: false,
            Model: "ollama",
            Content: string.Empty,
            PromptTokens: null,
            CompletionTokens: null,
            Latency: latency,
            Error: error);

    private sealed record OllamaGenerateRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("prompt")] string Prompt,
        [property: JsonPropertyName("system")] string? System,
        [property: JsonPropertyName("stream")] bool Stream,
        [property: JsonPropertyName("options")] OllamaOptionsPayload Options);

    private sealed record OllamaOptionsPayload(
        [property: JsonPropertyName("num_ctx")] int NumCtx,
        [property: JsonPropertyName("temperature")] double Temperature,
        [property: JsonPropertyName("num_predict")] int NumPredict);

    private sealed record OllamaGenerateResponse(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("response")] string? Response,
        [property: JsonPropertyName("prompt_eval_count")] int? PromptEvalCount,
        [property: JsonPropertyName("eval_count")] int? EvalCount);
}

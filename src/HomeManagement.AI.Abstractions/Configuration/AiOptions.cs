using System.ComponentModel.DataAnnotations;

namespace HomeManagement.AI.Abstractions.Configuration;

public sealed class AiOptions
{
    public const string SectionName = "AI";

    public bool Enabled { get; set; }

    [Required]
    public string Provider { get; set; } = "Disabled";

    [Range(1, 32)]
    public int MaxConcurrentRequests { get; set; } = 2;

    [Range(1, 300)]
    public int DefaultTimeoutSeconds { get; set; } = 60;

    public OllamaOptions Ollama { get; set; } = new();
}

public sealed class OllamaOptions
{
    [Required]
    public string BaseUrl { get; set; } = "http://zombox.cowgomu.net:11434";

    [Required]
    public string Model { get; set; } = "qwen2.5:7b-instruct-q4_K_M";

    [Range(1, 300)]
    public int TimeoutSeconds { get; set; } = 90;

    [Range(32, 16384)]
    public int NumCtx { get; set; } = 4096;

    [Range(32, 8192)]
    public int MaxTokens { get; set; } = 1024;

    [Range(0.0, 2.0)]
    public double Temperature { get; set; } = 0.2;
}

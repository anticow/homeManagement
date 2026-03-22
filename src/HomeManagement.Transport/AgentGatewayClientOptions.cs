using Microsoft.Extensions.Configuration;

namespace HomeManagement.Transport;

public sealed class AgentGatewayClientOptions
{
    public const string SectionName = "AgentGateway";

    public string BaseUrl { get; set; } = "http://localhost:9444";

    public string ApiKey { get; set; } = string.Empty;

    public int PollIntervalSeconds { get; set; } = 5;

    public static AgentGatewayClientOptions FromConfiguration(IConfiguration? configuration)
    {
        var section = configuration?.GetSection(SectionName);
        return new AgentGatewayClientOptions
        {
            BaseUrl = section?[nameof(BaseUrl)] ?? "http://localhost:9444",
            ApiKey = section?[nameof(ApiKey)] ?? string.Empty,
            PollIntervalSeconds = int.TryParse(section?[nameof(PollIntervalSeconds)], out var pollInterval)
                ? Math.Max(1, pollInterval)
                : 5
        };
    }
}
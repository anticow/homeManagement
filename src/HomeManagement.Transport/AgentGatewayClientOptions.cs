namespace HomeManagement.Transport;

public sealed class AgentGatewayClientOptions
{
    public const string SectionName = "AgentGateway";

    public string BaseUrl { get; set; } = "http://localhost:9444";

    public string ApiKey { get; set; } = string.Empty;

    public int PollIntervalSeconds { get; set; } = 5;
}

namespace HomeManagement.Gateway;

/// <summary>
/// URLs for each platform component polled by the /platform-health endpoint.
/// Bound from the <c>PlatformHealth</c> configuration section.
/// </summary>
public sealed class PlatformHealthOptions
{
    public const string SectionName = "PlatformHealth";

    public string? BrokerUrl { get; set; }
    public string? AuthUrl { get; set; }
    public string? WebUrl { get; set; }
    public string? AgentGatewayUrl { get; set; }
    public string? SeqUrl { get; set; }
    public string? PrometheusUrl { get; set; }
    public string? GrafanaUrl { get; set; }
    public string? ArgoCDUrl { get; set; }
    public string? AwxUrl { get; set; }
}

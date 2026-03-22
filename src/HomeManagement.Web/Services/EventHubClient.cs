using Microsoft.AspNetCore.SignalR.Client;

namespace HomeManagement.Web.Services;

/// <summary>
/// SignalR client that connects to the Broker's EventHub for real-time updates.
/// </summary>
public sealed class EventHubClient : IAsyncDisposable
{
    private HubConnection? _connection;
    private readonly IConfiguration _configuration;

    public EventHubClient(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public event Action<string>? OnJobProgress;
    public event Action<string>? OnAgentStatus;
    public event Action<string>? OnAuditEvent;

    public async Task ConnectAsync(string accessToken, CancellationToken ct = default)
    {
        var brokerUrl = _configuration["BrokerApi:BaseUrl"] ?? "http://localhost:8082";

        _connection = new HubConnectionBuilder()
            .WithUrl($"{brokerUrl}/hubs/events", options =>
            {
                options.AccessTokenProvider = () => Task.FromResult<string?>(accessToken);
            })
            .WithAutomaticReconnect()
            .Build();

        _connection.On<string>("JobProgress", msg => OnJobProgress?.Invoke(msg));
        _connection.On<string>("AgentStatus", msg => OnAgentStatus?.Invoke(msg));
        _connection.On<string>("AuditEvent", msg => OnAuditEvent?.Invoke(msg));

        await _connection.StartAsync(ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
        {
            await _connection.DisposeAsync();
        }
    }
}

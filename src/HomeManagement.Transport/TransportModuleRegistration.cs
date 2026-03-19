using HomeManagement.Abstractions.CrossCutting;
using HomeManagement.Abstractions.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace HomeManagement.Transport;

public sealed class TransportModuleRegistration : IModuleRegistration
{
    public string ModuleName => "Transport";

    public void Register(IServiceCollection services)
    {
        // Provide default ResilienceOptions — consumers can override via IOptions<ResilienceOptions> binding
        services.AddOptions<ResilienceOptions>();
        services.AddSingleton<IResiliencePipeline, DefaultResiliencePipeline>();
        services.AddSingleton<IRemotePathResolver, RemotePathResolver>();
        services.AddSingleton<SshTransportProvider>();
        services.AddSingleton<WinRmTransportProvider>();
        services.AddSingleton<AgentTransportProvider>();
        // AgentGateway: single instance shared between IAgentGateway consumers and the gRPC service
        services.AddSingleton<AgentGatewayService>();
        services.AddSingleton<IAgentGateway>(sp => sp.GetRequiredService<AgentGatewayService>());
        // Command broker: async queue for fire-and-forget command dispatch with persistent results
        services.AddSingleton<CommandBrokerService>();
        services.AddSingleton<ICommandBroker>(sp => sp.GetRequiredService<CommandBrokerService>());
        services.AddScoped<IRemoteExecutor, RemoteExecutorRouter>();
    }
}

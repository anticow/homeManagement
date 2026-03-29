using HomeManagement.Abstractions.CrossCutting;
using HomeManagement.Abstractions.Interfaces;
using Microsoft.Extensions.Hosting;
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
        services.AddSingleton<RemoteAgentGatewayClient>();
        services.AddSingleton<IAgentGateway>(sp => sp.GetRequiredService<RemoteAgentGatewayClient>());
        services.AddHostedService<AgentAutoRegistrationHostedService>();
        // Command broker: async queue for fire-and-forget command dispatch with persistent results
        services.AddSingleton<CommandBrokerService>();
        services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<CommandBrokerService>());
        services.AddSingleton<ICommandBroker>(sp => sp.GetRequiredService<CommandBrokerService>());
        services.AddScoped<IRemoteExecutor, RemoteExecutorRouter>();
    }
}

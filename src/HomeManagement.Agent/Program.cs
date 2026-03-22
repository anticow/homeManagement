using System.Globalization;
using HomeManagement.Agent.Communication;
using HomeManagement.Agent.Configuration;
using HomeManagement.Agent.Handlers;
using HomeManagement.Agent.Resilience;
using HomeManagement.Agent.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Serilog;

namespace HomeManagement.Agent;

internal static class Program
{
    public static async Task Main(string[] args)
    {
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.Console(formatProvider: CultureInfo.InvariantCulture)
            .WriteTo.File("logs/agent-.log",
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7,
                formatProvider: CultureInfo.InvariantCulture)
            .CreateLogger();

        try
        {
            var host = Host.CreateDefaultBuilder(args)
                .ConfigureAppConfiguration((_, config) =>
                {
                    config.AddJsonFile("hm-agent.json", optional: true, reloadOnChange: false);
                })
                .ConfigureServices((context, services) =>
                {
                    // Configuration — bound from hm-agent.json / env vars
                    var agentConfig = new AgentConfiguration();
                    context.Configuration.GetSection(AgentConfiguration.SectionName).Bind(agentConfig);

                    // Default AgentId to machine name if not configured
                    if (string.IsNullOrEmpty(agentConfig.AgentId))
                        agentConfig.AgentId = Environment.MachineName.ToLowerInvariant();

                    services.AddSingleton(Options.Create(agentConfig));

                    // Security
                    services.AddSingleton<CertificateLoader>();
                    services.AddSingleton<CommandValidator>();
                    services.AddSingleton<IntegrityChecker>();

                    // Communication
                    services.AddSingleton<GrpcChannelManager>();
                    services.AddSingleton<CommandDispatcher>();
                    services.AddSingleton(sp => new AgentCommandExecutionService(
                        sp.GetRequiredService<CommandDispatcher>(),
                        sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<AgentCommandExecutionService>>(),
                        sp.GetRequiredService<IOptions<AgentConfiguration>>().Value.MaxConcurrentCommands));
                    services.AddSingleton<ShutdownCoordinator>();

                    // Resilience
                    services.AddSingleton<ReconnectPolicy>();

                    // Command handlers
                    services.AddSingleton<PatchCommandHandler>();
                    services.AddSingleton<ICommandHandler, ShellCommandHandler>();
                    services.AddSingleton<ICommandHandler, SystemInfoHandler>();
                    services.AddSingleton<ICommandHandler, ServiceCommandHandler>();
                    services.AddSingleton<ICommandHandler, PatchCommandHandler>(sp => sp.GetRequiredService<PatchCommandHandler>());
                    services.AddSingleton<ICommandHandler, PatchApplyCommandHandler>();

                    // Update handler
                    services.AddSingleton<UpdateCommandHandler>();

                    // Hosted service — the main connection loop
                    services.AddHostedService<AgentHostService>();
                })
                .Build();

            await host.RunAsync();
        }
        finally
        {
            await Log.CloseAndFlushAsync();
        }
    }
}

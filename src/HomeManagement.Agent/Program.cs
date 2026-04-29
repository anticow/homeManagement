using System.Globalization;
using HomeManagement.Agent.Communication;
using HomeManagement.Agent.Configuration;
using HomeManagement.Agent.Handlers;
using HomeManagement.Agent.Resilience;
using HomeManagement.Agent.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Serilog;

namespace HomeManagement.Agent;

internal static class Program
{
    public static async Task Main(string[] args)
    {
        // Minimal bootstrap logger (console only) — replaced once config is loaded
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.Console(formatProvider: CultureInfo.InvariantCulture)
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
                    // ── Logging — configure Serilog from loaded config ──
                    var agentSection = context.Configuration.GetSection(AgentConfiguration.SectionName);
                    var seqUrl = agentSection["SeqUrl"] ?? string.Empty;
                    var logLevel = Enum.TryParse<Serilog.Events.LogEventLevel>(
                        agentSection["LogLevel"] ?? "Information", out var lvl)
                        ? lvl : Serilog.Events.LogEventLevel.Information;
                    var retentionDays = int.TryParse(agentSection["LogRetentionDays"], out var rd) ? rd : 7;

                    var logPath = Path.Combine(AppContext.BaseDirectory, "logs", "agent-.log");

                    var logConfig = new LoggerConfiguration()
                        .MinimumLevel.Is(logLevel)
                        .MinimumLevel.Override("Microsoft", Serilog.Events.LogEventLevel.Warning)
                        .MinimumLevel.Override("System", Serilog.Events.LogEventLevel.Warning)
                        .Enrich.WithProperty("Service", "hm-agent")
                        .Enrich.WithMachineName()
                        .Enrich.WithThreadId()
                        .WriteTo.Console(formatProvider: CultureInfo.InvariantCulture)
                        .WriteTo.File(logPath,
                            rollingInterval: RollingInterval.Day,
                            retainedFileCountLimit: retentionDays,
                            fileSizeLimitBytes: 50L * 1024 * 1024,
                            formatProvider: CultureInfo.InvariantCulture);

                    if (!string.IsNullOrWhiteSpace(seqUrl))
                        logConfig = logConfig.WriteTo.Seq(seqUrl);

                    Log.Logger = logConfig.CreateLogger();
                    services.AddLogging(logging =>
                    {
                        logging.ClearProviders();
                        logging.AddSerilog(dispose: true);
                    });

                    // ── Configuration — bound from hm-agent.json / env vars ──
                    var agentConfig = new AgentConfiguration();
                    agentSection.Bind(agentConfig);

                    // Default AgentId to machine name if not configured
                    if (string.IsNullOrEmpty(agentConfig.AgentId))
                        agentConfig.AgentId = Environment.MachineName.ToLowerInvariant();

                    agentConfig.Validate();

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

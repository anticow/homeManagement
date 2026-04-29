using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using HomeManagement.Abstractions.Interfaces;
using HomeManagement.Core;
using HomeManagement.Gui.Services;
using HomeManagement.Gui.ViewModels;
using HomeManagement.Integration.Prometheus;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;

namespace HomeManagement.Gui;

#pragma warning disable CA1001 // Disposed in ShutdownRequested handler
public class App : Application
#pragma warning restore CA1001
{
    private ServiceProvider? _serviceProvider;
    private AgentAutoRegistrationService? _agentRegistration;
    private Transport.CommandBrokerService? _commandBroker;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            try
            {
                var dataDir = GetDataDirectory();
                Directory.CreateDirectory(dataDir);

                var configuration = new ConfigurationBuilder()
                    .SetBasePath(AppDomain.CurrentDomain.BaseDirectory)
                    .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
                    .AddEnvironmentVariables()
                    .Build();

                var platformOptions = new DesktopPlatformOptions
                {
                    BrokerBaseUrl = configuration["BrokerApi:BaseUrl"] ?? string.Empty,
                    AuthBaseUrl = configuration["AuthApi:BaseUrl"] ?? string.Empty,
                    Username = configuration["DesktopAuth:Username"]
                };

                var services = new ServiceCollection();
                services.AddSingleton<IConfiguration>(configuration);
                services.AddSingleton(platformOptions);
                services.AddHomeManagementLogging(dataDir);

                if (platformOptions.IsEnabled)
                {
                    services.AddDesktopPlatformClients(platformOptions);
                }
                else
                {
                    services.AddHomeManagement(dataDir);
                }

                // Prometheus endpoint state — enabled via Prometheus:Enabled=true in appsettings.json.
                // When disabled (default), a no-op provider is registered so ViewModels get null-safe metrics.
                services.AddPrometheusIntegration(configuration);

                // GUI services
                services.AddSingleton<NavigationService>();
                services.AddSingleton<IDialogService, DialogService>();
                services.AddSingleton<IClipboardService, ClipboardService>();
                services.AddSingleton<IIdleTimerService, IdleTimerService>();
                services.AddSingleton<MainWindowViewModel>();

                // Page ViewModels — transient (fresh state per navigation)
                services.AddTransient<DashboardViewModel>();
                services.AddTransient<MachinesViewModel>();
                services.AddTransient<MachineDetailViewModel>();
                services.AddTransient<PatchingViewModel>();
                services.AddTransient<ServicesViewModel>();
                services.AddTransient<JobsViewModel>();
                services.AddTransient<JobDetailViewModel>();
                services.AddTransient<CredentialsViewModel>();
                services.AddTransient<VaultUnlockViewModel>();
                services.AddTransient<AuditLogViewModel>();
                services.AddTransient<SettingsViewModel>();

                _serviceProvider = services.BuildServiceProvider();

                // Initialize database schema before resolving any ViewModels
                if (!platformOptions.IsEnabled)
                {
                    ServiceRegistration.InitializeDatabaseAsync(_serviceProvider)
                        .GetAwaiter().GetResult();
                }

                var mainVm = _serviceProvider.GetRequiredService<MainWindowViewModel>();
                desktop.MainWindow = new MainWindow { DataContext = mainVm };

                var agentGateway = _serviceProvider.GetRequiredService<IAgentGateway>();
                agentGateway.StartAsync(CancellationToken.None).GetAwaiter().GetResult();

                if (!platformOptions.IsEnabled)
                {
                    _agentRegistration = new AgentAutoRegistrationService(
                        agentGateway,
                        _serviceProvider.GetRequiredService<IServiceScopeFactory>(),
                        _serviceProvider.GetRequiredService<ILogger<AgentAutoRegistrationService>>());
                    _agentRegistration.Start();

                    // Start the async command broker for fire-and-forget operation dispatch
                    _commandBroker = _serviceProvider.GetRequiredService<Transport.CommandBrokerService>();
                    _commandBroker.Start();
                }

                // Dispose DI container on shutdown to clean up singletons and DB connections
                desktop.ShutdownRequested += (_, _) =>
                {
                    _agentRegistration?.Dispose();
                    _agentRegistration = null;
                    _commandBroker?.DisposeAsync().AsTask().GetAwaiter().GetResult();
                    _commandBroker = null;
                    agentGateway.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
                    _serviceProvider?.Dispose();
                    _serviceProvider = null;
                    Log.CloseAndFlush();
                };
            }
#pragma warning disable CA1031 // Must show error to user on startup failure
            catch (Exception ex)
#pragma warning restore CA1031
            {
                Log.Fatal(ex, "Application startup failed");
                Log.CloseAndFlush();

                // Show a minimal error window so the user has diagnostic info
                desktop.MainWindow = new Window
                {
                    Title = "HomeManagement — Startup Error",
                    Width = 600,
                    Height = 200,
                    Content = new TextBlock
                    {
                        Text = $"Failed to start: {ex.Message}\n\nSee logs for details.",
                        Margin = new Avalonia.Thickness(20),
                        TextWrapping = Avalonia.Media.TextWrapping.Wrap
                    }
                };
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static string GetDataDirectory()
    {
        if (OperatingSystem.IsWindows())
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "HomeManagement");

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".local", "share", "homemanagement");
    }
}

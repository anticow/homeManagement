using System.Globalization;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using HomeManagement.Abstractions.CrossCutting;
using HomeManagement.Abstractions.Repositories;
using HomeManagement.Data;
using HomeManagement.Data.Repositories;
using Serilog;

namespace HomeManagement.Core;

/// <summary>
/// Composition root — registers infrastructure services, discovers modules, and wires the application.
/// </summary>
public static class ServiceRegistration
{
    /// <summary>
    /// Register all HomeManagement infrastructure and discover module registrations.
    /// </summary>
    public static IServiceCollection AddHomeManagement(
        this IServiceCollection services,
        string dataDirectory,
        IEnumerable<Assembly>? moduleAssemblies = null)
    {
        var dbPath = Path.Combine(dataDirectory, "homemanagement.db");

        // ── Data layer ──
        services.AddDbContext<HomeManagementDbContext>(options =>
            options.UseSqlite($"Data Source={dbPath}"));

        // ── Cross-cutting infrastructure ──
        services.AddSingleton<ICorrelationContext, CorrelationContext>();
        services.AddSingleton<ISystemHealthService, SystemHealthService>();

        // ── Repository layer ──
        services.AddScoped<IMachineRepository, MachineRepository>();
        services.AddScoped<IPatchHistoryRepository, PatchHistoryRepository>();
        services.AddScoped<IAuditEventRepository, AuditEventRepository>();
        services.AddScoped<IJobRepository, JobRepository>();
        services.AddScoped<IServiceSnapshotRepository, ServiceSnapshotRepository>();
        services.AddScoped<IUnitOfWork, UnitOfWork>();

        // ── Module discovery ──
        // Each module assembly contains an IModuleRegistration implementation that self-registers.
        // Force-load referenced assemblies from the output directory so the scan finds them,
        // because .NET lazy-loads assemblies and they may not yet be in the AppDomain.
        var assemblies = moduleAssemblies ?? LoadReferencedAssemblies();
        foreach (var assembly in assemblies)
        {
            IEnumerable<Type> registrationTypes;
            try
            {
                registrationTypes = assembly.GetTypes()
                    .Where(t => t is { IsAbstract: false, IsInterface: false }
                             && typeof(IModuleRegistration).IsAssignableFrom(t));
            }
            catch (ReflectionTypeLoadException)
            {
                // Assembly has unresolvable types — skip it
                continue;
            }

            foreach (var regType in registrationTypes)
            {
                try
                {
                    if (Activator.CreateInstance(regType) is IModuleRegistration registration)
                    {
                        registration.Register(services);
                    }
                }
#pragma warning disable CA1031 // Log and fail fast with context
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    throw new InvalidOperationException(
                        $"Module registration failed for '{regType.FullName}' in assembly '{assembly.GetName().Name}'. " +
                        $"See inner exception for details.", ex);
                }
            }
        }

        return services;
    }

    /// <summary>
    /// Load all HomeManagement.* assemblies from the application base directory to ensure
    /// module registrations are discoverable. .NET lazy-loads referenced assemblies, so they
    /// may not be in the AppDomain when <see cref="AddHomeManagement"/> scans for modules.
    /// </summary>
    private static Assembly[] LoadReferencedAssemblies()
    {
        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        foreach (var dll in Directory.EnumerateFiles(baseDir, "HomeManagement.*.dll"))
        {
            try
            {
                var name = AssemblyName.GetAssemblyName(dll);
                if (AppDomain.CurrentDomain.GetAssemblies().All(a => a.GetName().Name != name.Name))
                {
                    Assembly.Load(name);
                }
            }
            catch (BadImageFormatException)
            {
                // Not a managed assembly — skip
            }
        }

        return AppDomain.CurrentDomain.GetAssemblies();
    }

    /// <summary>
    /// Run EF Core migrations and configure SQLite WAL mode for concurrent read performance.
    /// </summary>
    public static async Task InitializeDatabaseAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HomeManagementDbContext>();
        await db.Database.MigrateAsync();

        // Enable WAL mode for better concurrent read/write performance.
        // WAL allows readers to not block writers and vice versa.
        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
    }
}

/// <summary>
/// Bootstrap Serilog logging — called once at application startup, before DI container is built.
/// Separated from <see cref="ServiceRegistration"/> to avoid side-effects during DI registration.
/// </summary>
public static class LoggingBootstrap
{
    public static IServiceCollection AddHomeManagementLogging(
        this IServiceCollection services,
        string dataDirectory)
    {
        var logPath = Path.Combine(dataDirectory, "logs", "hm-.log");

        // Ensure log directory exists before configuring file sink
        var logDir = Path.GetDirectoryName(logPath);
        if (logDir is not null)
            Directory.CreateDirectory(logDir);

        // MED-05: Reduce retained log size to 10 files (1 GB max) and skip file sink
        // entirely if the target drive has less than 500 MB free.
        var retainedFiles = 10;
        var fileSizeLimit = 100L * 1024 * 1024; // 100 MB per file
        var enableFileSink = true;

        if (logDir is not null)
        {
            try
            {
                var driveInfo = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(logDir))!);
                if (driveInfo.IsReady && driveInfo.AvailableFreeSpace < 500L * 1024 * 1024)
                {
                    enableFileSink = false;
                    Console.Error.WriteLine(
                        $"[HomeManagement] WARNING: Less than 500 MB free on {driveInfo.Name} — file logging disabled.");
                }
            }
#pragma warning disable CA1031 // DriveInfo can fail on some platforms — proceed with defaults
            catch { /* best-effort check */ }
#pragma warning restore CA1031
        }

        var loggerConfig = new LoggerConfiguration()
            .MinimumLevel.Information()
            .Enrich.WithThreadId()
            .Enrich.WithMachineName()
            .Enrich.With<SensitivePropertyEnricher>()
            .WriteTo.Console(formatProvider: CultureInfo.InvariantCulture);

        if (enableFileSink)
        {
            loggerConfig = loggerConfig.WriteTo.File(logPath,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: retainedFiles,
                fileSizeLimitBytes: fileSizeLimit,
                formatProvider: CultureInfo.InvariantCulture);
        }

        Log.Logger = loggerConfig.CreateLogger();

        services.AddLogging(builder =>
        {
            builder.ClearProviders();
            builder.AddSerilog(dispose: true);
        });

        return services;
    }
}

using HomeManagement.Abstractions.CrossCutting;
using HomeManagement.Abstractions.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace HomeManagement.Auditing;

public sealed class AuditingModuleRegistration : IModuleRegistration
{
    public string ModuleName => "Auditing";

    public void Register(IServiceCollection services)
    {
        services.AddOptions<AuditOptions>();
        services.AddSingleton<ISensitiveDataFilter, SensitiveDataFilter>();
        services.AddScoped<IAuditLogger, AuditLoggerService>();
    }
}

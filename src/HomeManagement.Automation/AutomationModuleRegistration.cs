using HomeManagement.Abstractions.CrossCutting;
using HomeManagement.Abstractions.Interfaces;
using HomeManagement.Data.Repositories;
using Microsoft.Extensions.DependencyInjection;

namespace HomeManagement.Automation;

public sealed class AutomationModuleRegistration : IModuleRegistration
{
    public string ModuleName => "Automation";

    public void Register(IServiceCollection services)
    {
        services.AddOptions<AutomationOptions>();

        // Process runner abstraction (singleton — stateless)
        services.AddSingleton<IProcessRunner, DefaultProcessRunner>();

        // Data repositories (scoped — one per DI scope / request)
        services.AddScoped<IAutomationRunRepository, AutomationRunRepository>();
        services.AddScoped<IPlanRepository, PlanRepository>();

        // Planning infrastructure (scoped — ILLMClient may be scoped)
        services.AddScoped<IWorkflowPlanner, WorkflowPlanner>();
        services.AddScoped<IHaosAdapter, NullHaosAdapter>();
        services.AddScoped<IAnsibleHandoffService, GuardedAnsibleHandoffService>();

        // Engine (singleton — resolves scoped deps via CreateScope)
        services.AddSingleton<IAutomationEngine, AutomationEngine>();
    }
}

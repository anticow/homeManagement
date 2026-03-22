using HomeManagement.Abstractions.CrossCutting;
using HomeManagement.Abstractions.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Quartz;

namespace HomeManagement.Orchestration;

public sealed class OrchestrationModuleRegistration : IModuleRegistration
{
    public string ModuleName => "Orchestration";

    public void Register(IServiceCollection services)
    {
        // Register Quartz.NET scheduler
        services.AddQuartz();
        services.AddQuartzHostedService(options => options.WaitForJobsToComplete = true);

        services.AddSingleton<IJobScheduler, JobSchedulerService>();
    }
}

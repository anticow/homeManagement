using HomeManagement.Abstractions.Models;
namespace HomeManagement.Automation;

/// <summary>
/// Translates a natural-language objective into a structured <see cref="WorkflowPlan"/>
/// via the configured LLM.  The generated plan is NOT persisted or approved by this
/// interface — persistence and approval are the engine's responsibility.
/// </summary>
public interface IWorkflowPlanner
{
    Task<WorkflowPlan> CreatePlanAsync(CreatePlanRequest request, CancellationToken ct = default);
}


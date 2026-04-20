namespace HomeManagement.Automation;

/// <summary>
/// Evaluates a generated plan against the current allowlist/denylist policy and assigns
/// a risk level.  This is a pure in-memory evaluation — no I/O or persistence.
/// </summary>
public sealed class PlanPolicyEngine
{
    // Steps that may appear in plans without triggering a violation.
    private static readonly HashSet<PlanStepKind> AllowedKinds =
    [
        PlanStepKind.GatherMetrics,
        PlanStepKind.ListServices,
        PlanStepKind.RestartService,
        PlanStepKind.ApplyPatch,
    ];

    // Steps that are hard-blocked at the current policy level.
    // Presence of any of these causes the plan to be Rejected regardless of hash check.
    private static readonly HashSet<PlanStepKind> DeniedKinds =
    [
        PlanStepKind.ShutdownMachine,
        PlanStepKind.RunScript,
        PlanStepKind.Unknown,
    ];

    public static PolicyEvaluation Evaluate(IReadOnlyList<PlanStep> steps)
    {
        var violations = new List<string>();
        var riskLevel = PlanRiskLevel.Low;

        foreach (var step in steps)
        {
            if (DeniedKinds.Contains(step.Kind))
            {
                violations.Add(
                    $"Step '{step.Name}' uses denied kind '{step.Kind}'. " +
                    "ShutdownMachine, RunScript, and Unknown steps are not permitted in the current automation policy.");
            }
            else if (!AllowedKinds.Contains(step.Kind))
            {
                violations.Add(
                    $"Step '{step.Name}' uses unrecognised kind '{step.Kind}'.");
            }

            // Elevate risk according to step kind.
            riskLevel = step.Kind switch
            {
                PlanStepKind.ApplyPatch when riskLevel < PlanRiskLevel.High => PlanRiskLevel.High,
                PlanStepKind.RestartService when riskLevel < PlanRiskLevel.Medium => PlanRiskLevel.Medium,
                PlanStepKind.ShutdownMachine or PlanStepKind.RunScript => PlanRiskLevel.Critical,
                _ => riskLevel,
            };
        }

        return new PolicyEvaluation(
            Allowed: violations.Count == 0,
            RiskLevel: riskLevel,
            Violations: violations);
    }
}

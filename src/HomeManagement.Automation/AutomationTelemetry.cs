using System.Diagnostics.Metrics;

namespace HomeManagement.Automation;

internal static class AutomationTelemetry
{
    public const string MeterName = "HomeManagement.Automation";

    private static readonly Meter Meter = new(MeterName, "1.0.0");

    private static readonly Counter<long> WorkflowRunsStarted =
        Meter.CreateCounter<long>("automation.workflow_runs_started");

    private static readonly Counter<long> WorkflowRunsCompleted =
        Meter.CreateCounter<long>("automation.workflow_runs_completed");

    private static readonly Histogram<double> WorkflowRunDurationMs =
        Meter.CreateHistogram<double>("automation.workflow_run_duration_ms", unit: "ms");

    private static readonly Counter<long> WorkflowStepFailures =
        Meter.CreateCounter<long>("automation.workflow_step_failures");

    private static readonly Counter<long> WorkflowMachineOutcomes =
        Meter.CreateCounter<long>("automation.workflow_machine_outcomes");

    public static void RecordRunStarted(string workflow)
    {
        WorkflowRunsStarted.Add(1, new KeyValuePair<string, object?>("workflow", workflow));
    }

    public static void RecordRunCompleted(string workflow, bool success, double durationMs)
    {
        var outcome = success ? "success" : "failure";
        WorkflowRunsCompleted.Add(
            1,
            new KeyValuePair<string, object?>("workflow", workflow),
            new KeyValuePair<string, object?>("outcome", outcome));

        WorkflowRunDurationMs.Record(
            durationMs,
            new KeyValuePair<string, object?>("workflow", workflow),
            new KeyValuePair<string, object?>("outcome", outcome));
    }

    public static void RecordStepFailure(string workflow, string step)
    {
        WorkflowStepFailures.Add(
            1,
            new KeyValuePair<string, object?>("workflow", workflow),
            new KeyValuePair<string, object?>("step", step));
    }

    public static void RecordMachineOutcome(string workflow, bool success)
    {
        WorkflowMachineOutcomes.Add(
            1,
            new KeyValuePair<string, object?>("workflow", workflow),
            new KeyValuePair<string, object?>("success", success));
    }
}

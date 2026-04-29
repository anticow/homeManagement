using HomeManagement.Abstractions.Models;
using System.Text;

namespace HomeManagement.Automation;

internal interface IAnsibleHandoffService
{
    Task<AnsibleHandoffExecutionResult> ExecuteAsync(AnsibleHandoffRunRequest request, CancellationToken ct = default);
}

internal sealed record AnsibleHandoffExecutionResult(
    bool Success,
    string Operation,
    string Playbook,
    string CommandLine,
    DateTime StartedUtc,
    DateTime CompletedUtc,
    int? ExitCode,
    string StdOut,
    string StdErr,
    string? ErrorMessage,
    bool TimedOut = false,
    bool Cancelled = false);

internal sealed class GuardedAnsibleHandoffService : IAnsibleHandoffService
{
    private static readonly Dictionary<string, string> AllowedOperations = new(StringComparer.OrdinalIgnoreCase)
    {
        ["proxmox.vm.provision"] = "provision-proxmox-vm.yml",
        ["k3s.control-plane.create"] = "create-k3s-control-plane.yml",
        ["k3s.control-plane.resume"] = "resume-k3s-control-plane.yml",
        ["k3s.worker.add"] = "add-k3s-worker.yml",
        ["infrastructure.remediate"] = "remediate-infra.yml",
    };

    private readonly IProcessRunner _processRunner;

    public GuardedAnsibleHandoffService(IProcessRunner processRunner)
    {
        _processRunner = processRunner;
    }

    public async Task<AnsibleHandoffExecutionResult> ExecuteAsync(AnsibleHandoffRunRequest request, CancellationToken ct = default)
    {
        var startedUtc = DateTime.UtcNow;
        var operation = request.Operation.Trim();

        if (!request.ApproveAndRun)
        {
            return Failure(request, startedUtc, "ApproveAndRun must be true to execute Ansible handoff.");
        }

        if (string.IsNullOrWhiteSpace(request.ApprovedBy) || string.IsNullOrWhiteSpace(request.ApprovalReason))
        {
            return Failure(request, startedUtc, "ApprovedBy and ApprovalReason are required.");
        }

        if (!AllowedOperations.TryGetValue(operation, out var playbook))
        {
            return Failure(request, startedUtc, $"Operation '{request.Operation}' is not allowlisted.");
        }

        var command = BuildCommand(playbook, request.TargetScope, request.ExtraVarsJson, request.DryRun);

        if (request.DryRun)
        {
            return new AnsibleHandoffExecutionResult(
                Success: true,
                Operation: operation,
                Playbook: playbook,
                CommandLine: command,
                StartedUtc: startedUtc,
                CompletedUtc: DateTime.UtcNow,
                ExitCode: 0,
                StdOut: "Dry-run completed; no ansible command executed.",
                StdErr: string.Empty,
                ErrorMessage: null);
        }

        var ansibleRoot = ResolveAnsibleRoot();
        if (ansibleRoot is null)
        {
            return Failure(request, startedUtc, "Unable to locate ansible workspace root.");
        }

        var playbookPath = Path.Combine(ansibleRoot, playbook);
        if (!File.Exists(playbookPath))
        {
            return Failure(request, startedUtc, $"Playbook '{playbook}' was not found.");
        }

        if (request.ExecutionTimeoutSeconds.HasValue && request.ExecutionTimeoutSeconds.Value is < 5 or > 3600)
        {
            return Failure(request, startedUtc, "ExecutionTimeoutSeconds must be between 5 and 3600 when provided.");
        }

        if (request.ExecutionTimeoutSeconds.HasValue && !request.CancelOnTimeout)
        {
            return Failure(request, startedUtc, "CancelOnTimeout must be true when ExecutionTimeoutSeconds is provided.");
        }

        using var timeoutCts = BuildTimeoutCancellationSource(request.ExecutionTimeoutSeconds, ct);
        var operationCt = timeoutCts?.Token ?? ct;

        return await RunProcessAsync(operation, playbook, command, ansibleRoot, startedUtc, _processRunner, operationCt);
    }

    private static CancellationTokenSource? BuildTimeoutCancellationSource(
        int? executionTimeoutSeconds,
        CancellationToken ct)
    {
        if (!executionTimeoutSeconds.HasValue)
        {
            return null;
        }

        var timeout = TimeSpan.FromSeconds(executionTimeoutSeconds.Value);
        var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        return timeoutCts;
    }

    private static async Task<AnsibleHandoffExecutionResult> RunProcessAsync(
        string operation,
        string playbook,
        string command,
        string ansibleRoot,
        DateTime startedUtc,
        IProcessRunner processRunner,
        CancellationToken ct)
    {
        try
        {
            var psCommand = $"-NoLogo -NoProfile -Command \"{command}\"";
            var result = await processRunner.RunAsync("pwsh", psCommand, ansibleRoot, ct);

            if (result.WasCancelled)
            {
                return new AnsibleHandoffExecutionResult(
                    Success: false,
                    Operation: operation,
                    Playbook: playbook,
                    CommandLine: command,
                    StartedUtc: startedUtc,
                    CompletedUtc: DateTime.UtcNow,
                    ExitCode: null,
                    StdOut: string.Empty,
                    StdErr: string.Empty,
                    ErrorMessage: "Ansible handoff execution was cancelled or timed out.",
                    TimedOut: true,
                    Cancelled: true);
            }

            var success = result.ExitCode == 0;
            return new AnsibleHandoffExecutionResult(
                Success: success,
                Operation: operation,
                Playbook: playbook,
                CommandLine: command,
                StartedUtc: startedUtc,
                CompletedUtc: DateTime.UtcNow,
                ExitCode: result.ExitCode,
                StdOut: result.StdOut,
                StdErr: result.StdErr,
                ErrorMessage: success ? null : "ansible-playbook exited with non-zero code.");
        }
        catch (Exception ex)
        {
            return new AnsibleHandoffExecutionResult(
                Success: false,
                Operation: operation,
                Playbook: playbook,
                CommandLine: command,
                StartedUtc: startedUtc,
                CompletedUtc: DateTime.UtcNow,
                ExitCode: null,
                StdOut: string.Empty,
                StdErr: string.Empty,
                ErrorMessage: ex.Message);
        }
    }

    private static string BuildCommand(string playbook, string? targetScope, string? extraVarsJson, bool dryRun)
    {
        var sb = new StringBuilder();
        sb.Append("ansible-playbook ");
        sb.Append('"').Append(playbook).Append('"');

        if (!string.IsNullOrWhiteSpace(targetScope))
        {
            sb.Append(" --limit ");
            sb.Append('"').Append(targetScope).Append('"');
        }

        if (!string.IsNullOrWhiteSpace(extraVarsJson))
        {
            sb.Append(" --extra-vars ");
            sb.Append('"').Append(extraVarsJson.Replace("\"", "\\\"")).Append('"');
        }

        if (dryRun)
        {
            sb.Append(" --check");
        }

        return sb.ToString();
    }

    private static string? ResolveAnsibleRoot()
    {
        var candidates = new[]
        {
            Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "..", "ansible")),
            Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "..", "..", "ansible")),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "ansible")),
            @"F:\git\ansible",
        };

        return candidates.FirstOrDefault(Directory.Exists);
    }

    private static AnsibleHandoffExecutionResult Failure(AnsibleHandoffRunRequest request, DateTime startedUtc, string message)
    {
        return new AnsibleHandoffExecutionResult(
            Success: false,
            Operation: request.Operation,
            Playbook: string.Empty,
            CommandLine: string.Empty,
            StartedUtc: startedUtc,
            CompletedUtc: DateTime.UtcNow,
            ExitCode: null,
            StdOut: string.Empty,
            StdErr: string.Empty,
            ErrorMessage: message);
    }
}


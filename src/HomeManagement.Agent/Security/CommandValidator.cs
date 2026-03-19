using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using HomeManagement.Agent.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HomeManagement.Agent.Security;

/// <summary>
/// Validates commands against an allowlist, rate limiter, and elevation guard.
/// </summary>
public sealed class CommandValidator
{
    private static readonly HashSet<string> AllowedCommandTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Shell", "PatchScan", "PatchApply", "ServiceControl", "SystemInfo"
    };

    private readonly AgentConfiguration _config;
    private readonly ILogger<CommandValidator> _logger;
    private readonly Regex[] _deniedPatterns;

    // Sliding-window rate limiter state using Stopwatch for monotonic time
    private readonly ConcurrentQueue<long> _requestTicks = new();
    private readonly object _rateLimitLock = new();

    public CommandValidator(IOptions<AgentConfiguration> config, ILogger<CommandValidator> logger)
    {
        _config = config.Value;
        _logger = logger;
        _deniedPatterns = _config.DeniedCommandPatterns
            .Select(p => new Regex(p, RegexOptions.Compiled, TimeSpan.FromSeconds(1)))
            .ToArray();
    }

    // Maximum command text length before regex evaluation — prevents ReDoS on oversized input
    private const int MaxCommandTextLength = 32_768;

    public CommandValidationResult Validate(string commandType, string? commandText, string elevationMode)
    {
        // 1. Command type allowlist
        if (!AllowedCommandTypes.Contains(commandType))
        {
            _logger.LogWarning("Rejected unknown command type: {CommandType}", commandType);
            return CommandValidationResult.Rejected("Authorization", $"Unknown command type: {commandType}");
        }

        // 2. Elevation guard
        if (!string.Equals(elevationMode, "None", StringComparison.OrdinalIgnoreCase) && !_config.AllowElevation)
        {
            _logger.LogWarning("Rejected elevated command — elevation not permitted by config");
            return CommandValidationResult.Rejected("Authorization", "Elevation is disabled on this agent.");
        }

        // 3. Shell command blocklist — with input length guard to limit regex CPU time
        if (string.Equals(commandType, "Shell", StringComparison.OrdinalIgnoreCase) && commandText is not null)
        {
            if (commandText.Length > MaxCommandTextLength)
            {
                _logger.LogWarning("Rejected shell command exceeding max length: {Length}", commandText.Length);
                return CommandValidationResult.Rejected("Authorization",
                    $"Command text exceeds maximum length of {MaxCommandTextLength} characters.");
            }

            foreach (var pattern in _deniedPatterns)
            {
                try
                {
                    if (pattern.IsMatch(commandText))
                    {
                        _logger.LogWarning("Rejected shell command matching denied pattern: {Pattern}", pattern);
                        return CommandValidationResult.Rejected("Authorization", "Command matches denied pattern.");
                    }
                }
                catch (RegexMatchTimeoutException)
                {
                    _logger.LogWarning("Deny-pattern regex timed out for pattern: {Pattern}", pattern);
                    return CommandValidationResult.Rejected("Authorization",
                        "Command validation timed out — rejected for safety.");
                }
            }
        }

        // 4. Rate limiting (sliding window)
        if (!TryAcquireRateLimit())
        {
            _logger.LogWarning("Rate limit exceeded ({Limit}/s)", _config.CommandRateLimit);
            return CommandValidationResult.Rejected("Transient", "Rate limited — try again shortly.");
        }

        return CommandValidationResult.Allowed;
    }

    private bool TryAcquireRateLimit()
    {
        // Use Environment.TickCount64 for monotonic time (immune to clock adjustments)
        var now = Environment.TickCount64;
        var windowStartTicks = now - 1000; // 1 second window

        lock (_rateLimitLock)
        {
            // Drain expired entries
            while (_requestTicks.TryPeek(out var oldest) && oldest < windowStartTicks)
                _requestTicks.TryDequeue(out _);

            if (_requestTicks.Count >= _config.CommandRateLimit)
                return false;

            _requestTicks.Enqueue(now);
            return true;
        }
    }
}

public readonly record struct CommandValidationResult(bool IsAllowed, string? ErrorCategory, string? ErrorMessage)
{
    public static CommandValidationResult Allowed => new(true, null, null);
    public static CommandValidationResult Rejected(string category, string message) => new(false, category, message);
}

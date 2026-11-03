using HomeManagement.Abstractions.Models;

namespace HomeManagement.AgentGateway.Host.Services;

internal static class AgentEnrollmentIdentity
{
    public static string AgentId(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var id = value.ToLowerInvariant();
        if (id.Length is < 1 or > 63 || !IsAlphaNumeric(id[0])
            || id.Any(c => !IsAlphaNumeric(c) && c is not '.' and not '_' and not '-'))
            throw new ArgumentException("An agent ID must contain 1-63 ASCII identifier characters.");
        return id;
    }

    public static string Hostname(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var hostname = value.TrimEnd('.').ToLowerInvariant();
        if (value.EndsWith("..", StringComparison.Ordinal) || hostname.Length is < 1 or > 253
            || hostname.Split('.').Any(label => label.Length is < 1 or > 63
                || !IsAlphaNumeric(label[0]) || !IsAlphaNumeric(label[^1])
                || label.Any(c => !IsAlphaNumeric(c) && c != '-')))
            throw new ArgumentException("An explicit DNS hostname is required.");
        return hostname;
    }

    private static bool IsAlphaNumeric(char c) => c is >= 'a' and <= 'z' or >= '0' and <= '9';
}

internal sealed record AgentEnrollmentEntry(AgentEnrollmentMetadata Metadata, string Credential)
{
    public override string ToString() => $"AgentEnrollmentEntry {{ AgentId = {Metadata.AgentId}, Credential = [redacted] }}";
}

internal sealed record AgentEnrollmentDocument(
    Guid RegistryId, long Sequence, string AssignmentDigest, IReadOnlyList<AgentEnrollmentEntry> Entries);

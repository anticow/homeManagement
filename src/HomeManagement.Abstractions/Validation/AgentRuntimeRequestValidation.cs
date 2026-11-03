using HomeManagement.Abstractions.Models;

namespace HomeManagement.Abstractions.Validation;

public static class AgentRuntimeRequestValidation
{
    public static int ValidateCommand(RemoteCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.CommandText is null || string.IsNullOrWhiteSpace(command.CommandType)
            || !Enum.IsDefined(command.Elevation)
            || command.EnvironmentVariables?.Any(pair => string.IsNullOrEmpty(pair.Key) || pair.Value is null) == true)
            throw new ArgumentException("Agent command fields are invalid.");
        if (command.Timeout < TimeSpan.FromSeconds(1) || command.Timeout > TimeSpan.FromHours(24))
            throw new ArgumentException("Agent command timeout must be between one second and 24 hours.");
        return checked((int)Math.Ceiling(command.Timeout.TotalSeconds));
    }

    public static void ValidateUpdate(AgentUpdatePackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        if (string.IsNullOrWhiteSpace(package.Version) || package.BinarySha256 is null
            || package.BinarySha256.Length is not (0 or 32)
            || !Uri.TryCreate(package.DownloadUrl, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("https" or "http") || !string.IsNullOrEmpty(uri.UserInfo))
            throw new ArgumentException("Agent update requires a version, an HTTP(S) download URL without credentials, and an empty or 32-byte checksum.");
    }
}

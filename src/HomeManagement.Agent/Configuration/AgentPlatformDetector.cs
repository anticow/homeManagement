using System.Runtime.InteropServices;

namespace HomeManagement.Agent.Configuration;

/// <summary>
/// Detects the current platform's OS, architecture, and capabilities.
/// </summary>
public static class AgentPlatformDetector
{
    public static PlatformInfo Detect() => new(
        OsType: RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? DetectedOs.Windows : DetectedOs.Linux,
        Architecture: RuntimeInformation.OSArchitecture switch
        {
            System.Runtime.InteropServices.Architecture.X64 => "x64",
            System.Runtime.InteropServices.Architecture.Arm64 => "arm64",
            var arch => arch.ToString().ToLowerInvariant()
        },
        OsDescription: RuntimeInformation.OSDescription
    );

    public record PlatformInfo(DetectedOs OsType, string Architecture, string OsDescription);

    public enum DetectedOs { Windows, Linux }
}

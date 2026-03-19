using HomeManagement.Abstractions;
using HomeManagement.Abstractions.Interfaces;

namespace HomeManagement.Transport;

/// <summary>
/// Resolves remote file paths according to the target machine's OS conventions.
/// </summary>
internal sealed class RemotePathResolver : IRemotePathResolver
{
    public string NormalizePath(string path, OsType targetOs)
    {
        return targetOs switch
        {
            OsType.Windows => path.Replace('/', '\\'),
            OsType.Linux => path.Replace('\\', '/'),
            _ => path
        };
    }

    public string CombinePath(OsType targetOs, params string[] segments)
    {
        var separator = GetSeparator(targetOs);
        return string.Join(separator, segments.Where(s => !string.IsNullOrEmpty(s)));
    }

    public char GetSeparator(OsType targetOs) => targetOs == OsType.Windows ? '\\' : '/';
}

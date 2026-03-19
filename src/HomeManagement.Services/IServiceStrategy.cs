using HomeManagement.Abstractions;
using HomeManagement.Abstractions.Models;
using HomeManagement.Abstractions.Validation;

namespace HomeManagement.Services;

/// <summary>
/// OS-specific service control strategy used internally by <see cref="ServiceControllerService"/>.
/// </summary>
internal interface IServiceStrategy
{
    OsType TargetOs { get; }
    string BuildStatusCommand(ServiceName serviceName);
    ServiceInfo ParseStatusOutput(string stdout, ServiceName serviceName);
    string BuildListCommand(ServiceFilter? filter);
    IReadOnlyList<ServiceInfo> ParseListOutput(string stdout);
    string BuildControlCommand(ServiceName serviceName, ServiceAction action);
}

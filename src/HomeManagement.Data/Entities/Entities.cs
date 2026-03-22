using HomeManagement.Abstractions;

namespace HomeManagement.Data.Entities;

public class MachineEntity
{
    public Guid Id { get; set; }
    public string Hostname { get; set; } = string.Empty;
    public string? Fqdn { get; set; }
    public string IpAddressesJson { get; set; } = "[]";
    public OsType OsType { get; set; }
    public string OsVersion { get; set; } = string.Empty;
    public MachineConnectionMode ConnectionMode { get; set; }
    public TransportProtocol Protocol { get; set; }
    public int Port { get; set; }
    public Guid CredentialId { get; set; }
    public MachineState State { get; set; }
    public int? CpuCores { get; set; }
    public long? RamBytes { get; set; }
    public string? Architecture { get; set; }
    public string? DisksJson { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
    public DateTime LastContactUtc { get; set; }
    public bool IsDeleted { get; set; }

    // Navigation properties
    public ICollection<MachineTagEntity> Tags { get; set; } = [];
    public ICollection<PatchHistoryEntity> PatchHistory { get; set; } = [];
    public ICollection<ServiceSnapshotEntity> ServiceSnapshots { get; set; } = [];
}

/// <summary>
/// Normalized tag storage — enables indexed queries like "find all machines tagged 'production'".
/// </summary>
public class MachineTagEntity
{
    public Guid Id { get; set; }
    public Guid MachineId { get; set; }
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;

    public MachineEntity Machine { get; set; } = null!;
}

public class PatchHistoryEntity
{
    public Guid Id { get; set; }
    public Guid MachineId { get; set; }
    public string PatchId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public PatchSeverity? Severity { get; set; }
    public PatchCategory? Category { get; set; }
    public long? SizeBytes { get; set; }
    public bool? RequiresReboot { get; set; }
    public PatchInstallState State { get; set; }
    public DateTime TimestampUtc { get; set; }
    public string? ErrorMessage { get; set; }
    public Guid? JobId { get; set; }

    public MachineEntity Machine { get; set; } = null!;
    public JobEntity? Job { get; set; }
}

public class AuditEventEntity
{
    public Guid EventId { get; set; }
    public DateTime TimestampUtc { get; set; }
    public string CorrelationId { get; set; } = string.Empty;
    public AuditAction Action { get; set; }
    public string ActorIdentity { get; set; } = string.Empty;
    public Guid? TargetMachineId { get; set; }
    public string? TargetMachineName { get; set; }
    public string? Detail { get; set; }
    public string? PropertiesJson { get; set; }
    public AuditOutcome Outcome { get; set; }
    public string? ErrorMessage { get; set; }
    public string? PreviousHash { get; set; }
    public string? EventHash { get; set; }
}

public class JobEntity
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public JobType Type { get; set; }
    public Abstractions.JobState State { get; set; }
    public DateTime SubmittedUtc { get; set; }
    public DateTime? StartedUtc { get; set; }
    public DateTime? CompletedUtc { get; set; }
    public int TotalTargets { get; set; }
    public int CompletedTargets { get; set; }
    public int FailedTargets { get; set; }
    public string? DefinitionJson { get; set; }
    public string? CorrelationId { get; set; }

    // Normalized — no more JSON blob for results
    public ICollection<JobMachineResultEntity> MachineResults { get; set; } = [];
}

/// <summary>
/// Normalized job results — enables queries like "find all jobs that failed on machine X".
/// </summary>
public class JobMachineResultEntity
{
    public Guid Id { get; set; }
    public Guid JobId { get; set; }
    public Guid MachineId { get; set; }
    public string MachineName { get; set; } = string.Empty;
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public long DurationMs { get; set; }

    public JobEntity Job { get; set; } = null!;
}

public class ScheduledJobEntity
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public JobType Type { get; set; }
    public string CronExpression { get; set; } = string.Empty;
    public string? DefinitionJson { get; set; }
    public bool IsEnabled { get; set; } = true;
    public DateTime? NextFireUtc { get; set; }
    public DateTime? LastFireUtc { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
}

/// <summary>
/// Point-in-time capture of a service's state on a machine.
/// </summary>
public class ServiceSnapshotEntity
{
    public Guid Id { get; set; }
    public Guid MachineId { get; set; }
    public string ServiceName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public ServiceState State { get; set; }
    public ServiceStartupType StartupType { get; set; }
    public int? ProcessId { get; set; }
    public DateTime CapturedUtc { get; set; }

    public MachineEntity Machine { get; set; } = null!;
}

/// <summary>
/// Key-value application configuration store.
/// </summary>
public class AppSettingEntity
{
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public DateTime UpdatedUtc { get; set; }
}

public class AuthUserEntity
{
    public Guid Id { get; set; }
    public string Username { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public string Provider { get; set; } = string.Empty;
    public DateTime CreatedUtc { get; set; }
    public DateTime? LastLoginUtc { get; set; }

    public ICollection<AuthUserRoleEntity> UserRoles { get; set; } = [];
    public ICollection<AuthRefreshTokenEntity> RefreshTokens { get; set; } = [];
}

public class AuthRoleEntity
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string PermissionsJson { get; set; } = "[]";

    public ICollection<AuthUserRoleEntity> UserRoles { get; set; } = [];
}

public class AuthUserRoleEntity
{
    public Guid UserId { get; set; }
    public Guid RoleId { get; set; }

    public AuthUserEntity User { get; set; } = null!;
    public AuthRoleEntity Role { get; set; } = null!;
}

public class AuthRefreshTokenEntity
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string TokenHash { get; set; } = string.Empty;
    public DateTime IssuedUtc { get; set; }
    public DateTime ExpiresUtc { get; set; }
    public DateTime? RevokedUtc { get; set; }

    public AuthUserEntity User { get; set; } = null!;
}

namespace HomeManagement.Abstractions;

// ── Machine & Platform ──
public enum OsType { Windows, Linux }
public enum MachineConnectionMode { Agentless, Agent }
public enum TransportProtocol { Ssh, WinRM, PSRemoting, Agent }
public enum MachineState { Online, Offline, Unreachable, Maintenance }

// ── Patching ──
public enum PatchSeverity { Critical, Important, Moderate, Low, Unclassified }
public enum PatchCategory { Security, BugFix, Feature, Driver, Other }
public enum PatchInstallState { Detected, Staged, Approved, Deferred, Installing, Installed, Failed }

// ── Services ──
public enum ServiceState { Running, Stopped, Starting, Stopping, Paused, Unknown }
public enum ServiceStartupType { Automatic, Manual, Disabled }
public enum ServiceAction { Start, Stop, Restart, Enable, Disable }

// ── Credentials ──
public enum CredentialType { Password, SshKey, SshKeyWithPassphrase, Kerberos }

// ── Remote Execution ──
public enum ElevationMode { None, Sudo, SudoAsUser, RunAsAdmin }

// ── Audit ──
public enum AuditAction
{
    MachineAdded, MachineRemoved, MachineMetadataRefreshed,
    PatchScanStarted, PatchScanCompleted,
    PatchApproved, PatchDeferred, PatchInstallStarted, PatchInstallCompleted, PatchInstallFailed,
    ServiceStarted, ServiceStopped, ServiceRestarted,
    CredentialCreated, CredentialUpdated, CredentialDeleted, CredentialAccessed,
    VaultUnlocked, VaultLocked,
    JobSubmitted, JobCompleted, JobFailed, JobCancelled,
    AgentConnected, AgentDisconnected, AgentUpdated,
    SettingsChanged
}
public enum AuditOutcome { Success, Failure, PartialSuccess }

// ── Jobs ──
public enum JobType { PatchScan, PatchApply, ServiceControl, MetadataRefresh, Custom }
public enum JobState { Queued, Running, Completed, Failed, Cancelled }

// ── Shared ──
public enum ExportFormat { Csv, Json }
public enum ErrorCategory { Transient, Authentication, Authorization, TargetError, ConfigurationError, SystemError }
public enum AgentConnectionEventType { Connected, Disconnected, HeartbeatTimeout }
public enum FileTransferDirection { Upload, Download }

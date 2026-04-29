using HomeManagement.Abstractions.Models;

namespace HomeManagement.Auth;

/// <summary>
/// Evaluates whether a user has the required permissions based on their roles.
/// </summary>
public sealed class RbacService
{

    // Default role definitions — can be overridden with database-backed roles.
    private static readonly HashSet<string> ViewerPermissions =
    [
        Permissions.MachinesRead,
        Permissions.PatchesRead,
        Permissions.ServicesRead,
        Permissions.JobsRead,
        Permissions.AuditRead
    ];

    private static readonly HashSet<string> OperatorPermissions =
    [
        Permissions.MachinesRead,
        Permissions.MachinesWrite,
        Permissions.PatchesRead,
        Permissions.PatchesApply,
        Permissions.ServicesRead,
        Permissions.ServicesControl,
        Permissions.JobsRead,
        Permissions.JobsSubmit,
        Permissions.JobsCancel,
        Permissions.CredentialsRead,
        Permissions.CredentialsWrite,
        Permissions.AuditRead,
        Permissions.AuditExport
    ];

    // Admin = all Operator permissions + user/settings administration.
    private static readonly HashSet<string> AdminPermissions =
    [
        ..OperatorPermissions,
        Permissions.AdminUsers,
        Permissions.AdminSettings
    ];

    // Auditor = all Viewer permissions + audit export.
    private static readonly HashSet<string> AuditorPermissions =
    [
        ..ViewerPermissions,
        Permissions.AuditExport
    ];

    private static readonly Dictionary<string, HashSet<string>> DefaultRoles = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Viewer"] = ViewerPermissions,
        ["Operator"] = OperatorPermissions,
        ["Admin"] = AdminPermissions,
        ["Auditor"] = AuditorPermissions
    };

    public RbacService() { }

    /// <summary>
    /// Check if the given roles collectively grant the required permission.
    /// </summary>
    public static bool HasPermission(IEnumerable<string> userRoles, string requiredPermission)
    {
        foreach (var role in userRoles)
        {
            if (DefaultRoles.TryGetValue(role, out var permissions) && permissions.Contains(requiredPermission))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Get all permissions for a set of roles.
    /// </summary>
    public static IReadOnlySet<string> GetEffectivePermissions(IEnumerable<string> userRoles)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var role in userRoles)
        {
            if (DefaultRoles.TryGetValue(role, out var permissions))
            {
                result.UnionWith(permissions);
            }
        }

        return result;
    }

    /// <summary>
    /// Get all default role definitions.
    /// </summary>
    public static IReadOnlyList<RoleDefinition> GetDefaultRoles()
    {
        return DefaultRoles.Select(kvp => new RoleDefinition(
            Guid.Empty, // Default roles don't have persisted IDs
            kvp.Key,
            $"Built-in {kvp.Key} role",
            kvp.Value.ToList()
        )).ToList();
    }
}

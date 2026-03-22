namespace HomeManagement.Auth;

/// <summary>
/// Configuration options for the authentication subsystem.
/// Bound from appsettings Auth section.
/// </summary>
public sealed class AuthOptions
{
    public const string SectionName = "Auth";

    /// <summary>RSA or symmetric key used to sign JWTs.</summary>
    public string JwtSigningKey { get; set; } = string.Empty;

    /// <summary>JWT issuer claim.</summary>
    public string Issuer { get; set; } = "homemanagement";

    /// <summary>JWT audience claim.</summary>
    public string Audience { get; set; } = "homemanagement-api";

    /// <summary>Access token lifetime.</summary>
    public TimeSpan AccessTokenLifetime { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>Refresh token lifetime.</summary>
    public TimeSpan RefreshTokenLifetime { get; set; } = TimeSpan.FromDays(7);

    /// <summary>Optional bootstrap admin seeding for first-run environments.</summary>
    public BootstrapAdminOptions BootstrapAdmin { get; set; } = new();

    /// <summary>Active Directory / LDAP configuration (null = disabled).</summary>
    public ActiveDirectoryOptions? ActiveDirectory { get; set; }
}

public sealed class BootstrapAdminOptions
{
    public bool Enabled { get; set; }
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string DisplayName { get; set; } = "Bootstrap Admin";
    public string Email { get; set; } = "admin@localhost";
}

public sealed class ActiveDirectoryOptions
{
    /// <summary>LDAP server host (e.g., ldap://dc01.corp.local).</summary>
    public string Server { get; set; } = string.Empty;

    /// <summary>Base DN for user searches (e.g., DC=corp,DC=local).</summary>
    public string BaseDn { get; set; } = string.Empty;

    /// <summary>LDAP attribute mapped to username (default: sAMAccountName).</summary>
    public string UserAttribute { get; set; } = "sAMAccountName";

    /// <summary>LDAP group attribute for role mapping.</summary>
    public string GroupAttribute { get; set; } = "memberOf";

    /// <summary>Use LDAPS (port 636) with TLS.</summary>
    public bool UseSsl { get; set; } = true;
}

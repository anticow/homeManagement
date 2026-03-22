using System.DirectoryServices.Protocols;
using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HomeManagement.Auth;

/// <summary>
/// Authenticates users against Active Directory via LDAP bind.
/// </summary>
public sealed class ActiveDirectoryProvider
{
    private readonly ActiveDirectoryOptions _options;
    private readonly ILogger<ActiveDirectoryProvider> _logger;

    public ActiveDirectoryProvider(IOptions<AuthOptions> authOptions, ILogger<ActiveDirectoryProvider> logger)
    {
        _options = authOptions.Value.ActiveDirectory
            ?? throw new InvalidOperationException("Active Directory is not configured.");
        _logger = logger;
    }

    /// <summary>
    /// Authenticate a user via LDAP simple bind and retrieve group membership.
    /// </summary>
    public ActiveDirectoryResult Authenticate(string username, string password)
    {
        try
        {
            var identifier = new LdapDirectoryIdentifier(_options.Server);
            var credential = new NetworkCredential(username, password);

            using var connection = new LdapConnection(identifier, credential);
            connection.SessionOptions.ProtocolVersion = 3;
            if (_options.UseSsl)
            {
                connection.SessionOptions.SecureSocketLayer = true;
            }

            connection.Bind();

            // Search for the user to get DN and groups
            var searchFilter = $"({_options.UserAttribute}={EscapeLdapFilter(username)})";
            var searchRequest = new SearchRequest(
                _options.BaseDn,
                searchFilter,
                SearchScope.Subtree,
                _options.UserAttribute, "displayName", "mail", _options.GroupAttribute);

            var response = (SearchResponse)connection.SendRequest(searchRequest);

            if (response.Entries.Count == 0)
            {
                _logger.LogWarning("AD bind succeeded but user search returned no entries for {Username}", username);
                return new ActiveDirectoryResult(false, Error: "User not found in directory.");
            }

            var entry = response.Entries[0];
            var displayName = GetAttributeValue(entry, "displayName") ?? username;
            var email = GetAttributeValue(entry, "mail") ?? string.Empty;
            var groups = GetAttributeValues(entry, _options.GroupAttribute);

            _logger.LogInformation("AD authentication succeeded for {Username} with {GroupCount} groups",
                username, groups.Count);

            return new ActiveDirectoryResult(true, displayName, email, groups);
        }
        catch (LdapException ex)
        {
            _logger.LogWarning(ex, "AD authentication failed for {Username}", username);
            return new ActiveDirectoryResult(false, Error: "Invalid credentials or LDAP error.");
        }
    }

    /// <summary>Escape special characters for LDAP filter to prevent injection.</summary>
    private static string EscapeLdapFilter(string input)
    {
        return input
            .Replace("\\", "\\5c", StringComparison.Ordinal)
            .Replace("*", "\\2a", StringComparison.Ordinal)
            .Replace("(", "\\28", StringComparison.Ordinal)
            .Replace(")", "\\29", StringComparison.Ordinal)
            .Replace("\0", "\\00", StringComparison.Ordinal);
    }

    private static string? GetAttributeValue(SearchResultEntry entry, string attributeName)
    {
        return entry.Attributes.Contains(attributeName)
            ? entry.Attributes[attributeName][0]?.ToString()
            : null;
    }

    private static List<string> GetAttributeValues(SearchResultEntry entry, string attributeName)
    {
        if (!entry.Attributes.Contains(attributeName))
            return [];

        var values = new List<string>();
        for (var i = 0; i < entry.Attributes[attributeName].Count; i++)
        {
            var val = entry.Attributes[attributeName][i]?.ToString();
            if (val is not null)
                values.Add(val);
        }
        return values;
    }
}

public sealed record ActiveDirectoryResult(
    bool Success,
    string? DisplayName = null,
    string? Email = null,
    IReadOnlyList<string>? Groups = null,
    string? Error = null);

using System.Text.RegularExpressions;

namespace HomeManagement.Abstractions.Validation;

/// <summary>
/// A validated hostname. Constructed via <see cref="TryCreate"/> — invalid values are rejected at parse time.
/// </summary>
public readonly partial struct Hostname : IEquatable<Hostname>
{
    // RFC 1123: letters, digits, hyphens, dots. Max 253 chars.
    [GeneratedRegex(@"^[a-zA-Z0-9]([a-zA-Z0-9\-\.]{0,251}[a-zA-Z0-9])?$", RegexOptions.Compiled)]
    private static partial Regex HostnamePattern();

    public string Value { get; }

    private Hostname(string value) => Value = value;

    public static Hostname Create(string value)
    {
        if (!TryCreate(value, out var hostname, out var error))
            throw new ArgumentException(error, nameof(value));
        return hostname;
    }

    public static bool TryCreate(string value, out Hostname result, out string? error)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            result = default;
            error = "Hostname cannot be empty.";
            return false;
        }

        if (value.Length > 253)
        {
            result = default;
            error = "Hostname exceeds 253 characters.";
            return false;
        }

        if (!HostnamePattern().IsMatch(value))
        {
            result = default;
            error = "Hostname contains invalid characters. Only letters, digits, hyphens, and dots are allowed.";
            return false;
        }

        result = new Hostname(value);
        error = null;
        return true;
    }

    public override string ToString() => Value;
    public bool Equals(Hostname other) => string.Equals(Value, other.Value, StringComparison.OrdinalIgnoreCase);
    public override bool Equals(object? obj) => obj is Hostname other && Equals(other);
    public override int GetHashCode() => StringComparer.OrdinalIgnoreCase.GetHashCode(Value);
    public static bool operator ==(Hostname left, Hostname right) => left.Equals(right);
    public static bool operator !=(Hostname left, Hostname right) => !left.Equals(right);
}

/// <summary>
/// A validated service name. Rejects empty, overly long, or shell-injection-prone values.
/// </summary>
public readonly partial struct ServiceName : IEquatable<ServiceName>
{
    // Allow alphanumeric, hyphens, underscores, dots, @. No shell metacharacters.
    [GeneratedRegex(@"^[a-zA-Z0-9][a-zA-Z0-9\-_\.@]{0,254}$", RegexOptions.Compiled)]
    private static partial Regex ServiceNamePattern();

    public string Value { get; }

    private ServiceName(string value) => Value = value;

    public static ServiceName Create(string value)
    {
        if (!TryCreate(value, out var name, out var error))
            throw new ArgumentException(error, nameof(value));
        return name;
    }

    public static bool TryCreate(string value, out ServiceName result, out string? error)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            result = default;
            error = "Service name cannot be empty.";
            return false;
        }

        if (!ServiceNamePattern().IsMatch(value))
        {
            result = default;
            error = "Service name contains invalid characters.";
            return false;
        }

        result = new ServiceName(value);
        error = null;
        return true;
    }

    public override string ToString() => Value;
    public bool Equals(ServiceName other) => string.Equals(Value, other.Value, StringComparison.OrdinalIgnoreCase);
    public override bool Equals(object? obj) => obj is ServiceName other && Equals(other);
    public override int GetHashCode() => StringComparer.OrdinalIgnoreCase.GetHashCode(Value);
    public static bool operator ==(ServiceName left, ServiceName right) => left.Equals(right);
    public static bool operator !=(ServiceName left, ServiceName right) => !left.Equals(right);
}

/// <summary>
/// A validated CIDR range (e.g., "192.168.1.0/24").
/// </summary>
public readonly partial struct CidrRange : IEquatable<CidrRange>
{
    [GeneratedRegex(@"^(\d{1,3}\.){3}\d{1,3}/\d{1,2}$", RegexOptions.Compiled)]
    private static partial Regex CidrPattern();

    public string Value { get; }

    private CidrRange(string value) => Value = value;

    public static CidrRange Create(string value)
    {
        if (!TryCreate(value, out var range, out var error))
            throw new ArgumentException(error, nameof(value));
        return range;
    }

    public static bool TryCreate(string value, out CidrRange result, out string? error)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            result = default;
            error = "CIDR range cannot be empty.";
            return false;
        }

        if (!CidrPattern().IsMatch(value))
        {
            result = default;
            error = "Invalid CIDR notation. Expected format: '192.168.1.0/24'.";
            return false;
        }

        var parts = value.Split('/');
        var octets = parts[0].Split('.');
        var prefix = int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);

        if (prefix > 32 || octets.Any(o => int.Parse(o, System.Globalization.CultureInfo.InvariantCulture) > 255))
        {
            result = default;
            error = "CIDR range has invalid octets or prefix length.";
            return false;
        }

        result = new CidrRange(value);
        error = null;
        return true;
    }

    public override string ToString() => Value;
    public bool Equals(CidrRange other) => string.Equals(Value, other.Value, StringComparison.Ordinal);
    public override bool Equals(object? obj) => obj is CidrRange other && Equals(other);
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);
    public static bool operator ==(CidrRange left, CidrRange right) => left.Equals(right);
    public static bool operator !=(CidrRange left, CidrRange right) => !left.Equals(right);
}

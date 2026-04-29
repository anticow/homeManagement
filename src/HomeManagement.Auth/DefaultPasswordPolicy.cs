using Microsoft.Extensions.Options;

namespace HomeManagement.Auth;

/// <summary>
/// Default password policy: configurable minimum length (default 12), at least one upper,
/// one lower, and one digit. Rules are sourced from <see cref="AuthOptions"/> so they can
/// be tuned without a code change.
/// </summary>
public sealed class DefaultPasswordPolicy : IPasswordPolicy
{
    private readonly AuthOptions _options;

    public DefaultPasswordPolicy(IOptions<AuthOptions> options) =>
        _options = options.Value;

    public void Validate(string password)
    {
        var minLength = _options.PasswordMinLength;

        if (string.IsNullOrEmpty(password) || password.Length < minLength)
            throw new InvalidOperationException($"Password must be at least {minLength} characters.");

        if (!password.Any(char.IsUpper))
            throw new InvalidOperationException("Password must contain at least one uppercase letter.");

        if (!password.Any(char.IsLower))
            throw new InvalidOperationException("Password must contain at least one lowercase letter.");

        if (!password.Any(char.IsDigit))
            throw new InvalidOperationException("Password must contain at least one digit.");
    }
}

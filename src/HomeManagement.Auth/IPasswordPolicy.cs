namespace HomeManagement.Auth;

/// <summary>
/// Enforces minimum password requirements before credentials are stored.
/// </summary>
public interface IPasswordPolicy
{
    /// <summary>
    /// Validates a plaintext password against the configured policy.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when the password does not meet requirements.</exception>
    void Validate(string password);
}

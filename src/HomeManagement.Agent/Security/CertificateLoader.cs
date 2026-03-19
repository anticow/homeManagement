using System.Security.Cryptography.X509Certificates;
using HomeManagement.Agent.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HomeManagement.Agent.Security;

/// <summary>
/// Loads and validates the agent's mTLS certificate and the CA trust anchor.
/// </summary>
public sealed class CertificateLoader
{
    private readonly AgentConfiguration _config;
    private readonly ILogger<CertificateLoader> _logger;

    public CertificateLoader(IOptions<AgentConfiguration> config, ILogger<CertificateLoader> logger)
    {
        _config = config.Value;
        _logger = logger;
    }

    /// <summary>
    /// Loads the agent identity certificate from the configured PFX path.
    /// Supports password-protected PFX files via AgentConfiguration.CertPassword.
    /// </summary>
    public X509Certificate2 LoadAgentCertificate()
    {
        var fullPath = Path.GetFullPath(_config.CertPath);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException($"Agent certificate not found at '{fullPath}'.");

        var cert = string.IsNullOrEmpty(_config.CertPassword)
            ? new X509Certificate2(fullPath)
            : new X509Certificate2(fullPath, _config.CertPassword,
                X509KeyStorageFlags.MachineKeySet | X509KeyStorageFlags.PersistKeySet);

        _logger.LogInformation("Loaded agent certificate: Subject={Subject}, Expires={Expires}",
            cert.Subject, cert.NotAfter);

        if (cert.NotAfter < DateTime.UtcNow)
            throw new InvalidOperationException($"Agent certificate expired on {cert.NotAfter:O}.");

        // Warn when certificate is nearing expiration (within 30 days)
        var daysUntilExpiry = (cert.NotAfter - DateTime.UtcNow).TotalDays;
        if (daysUntilExpiry < 30)
            _logger.LogWarning("Agent certificate expires in {Days:F0} days on {Expiry:O}",
                daysUntilExpiry, cert.NotAfter);

        return cert;
    }

    /// <summary>
    /// Loads the CA certificate used to validate the controller's identity.
    /// </summary>
    public X509Certificate2 LoadCaCertificate()
    {
        var fullPath = Path.GetFullPath(_config.CaCertPath);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException($"CA certificate not found at '{fullPath}'.");

        var cert = new X509Certificate2(fullPath);
        _logger.LogInformation("Loaded CA certificate: Subject={Subject}", cert.Subject);
        return cert;
    }

    /// <summary>
    /// Validates that the given certificate was signed by the expected CA.
    /// Uses CustomRootTrust with no revocation check — self-signed CAs
    /// do not have CRL/OCSP endpoints.
    /// </summary>
    public bool ValidateChain(X509Certificate2 certificate, X509Certificate2 caCertificate)
    {
        using var chain = new X509Chain();
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(caCertificate);

        var isValid = chain.Build(certificate);
        if (!isValid)
        {
            foreach (var status in chain.ChainStatus)
            {
                _logger.LogWarning("Certificate chain error: {Status} — {Information}",
                    status.Status, status.StatusInformation);
            }
        }

        return isValid;
    }
}

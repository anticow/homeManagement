using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace HomeManagement.Gateway;

/// <summary>
/// Factory methods for named <see cref="HttpClientHandler"/> instances used by the gateway.
/// Replaces <see cref="HttpClientHandler.DangerousAcceptAnyServerCertificateValidator"/>
/// with targeted trust: the in-cluster Kubernetes CA when running inside a pod, or the
/// system default trust store otherwise (development / testing).
/// </summary>
internal static class GatewayHttpHandlers
{
    /// <summary>Path to the Kubernetes service account CA cert injected into every pod.</summary>
    private const string K8sCaPath = "/var/run/secrets/kubernetes.io/serviceaccount/ca.crt";

    /// <summary>
    /// Creates an <see cref="HttpClientHandler"/> for the <c>k8s-api</c> named client.
    /// When running in-cluster, validates the API server certificate against the pod's
    /// injected CA cert instead of accepting any certificate.
    /// </summary>
    public static HttpClientHandler CreateK8sHandler()
    {
        var handler = new HttpClientHandler();

        if (File.Exists(K8sCaPath))
        {
            var caCert = LoadCaCert(K8sCaPath);
            if (caCert is not null)
            {
                handler.ServerCertificateCustomValidationCallback =
                    (_, cert, chain, errors) => ValidateAgainstCa(cert, chain, errors, caCert);
            }
        }
        // When K8sCaPath is absent (not in-cluster) the handler uses the system trust store.

        return handler;
    }

    /// <summary>
    /// Creates an <see cref="HttpClientHandler"/> for the <c>platform-health</c> named client.
    /// Auto-redirect is disabled because some platform services redirect HTTP→HTTPS in a way
    /// that re-triggers SSL errors — the health check treats 3xx as "service is alive".
    /// Certificate validation uses the in-cluster CA when available, otherwise system trust.
    /// </summary>
    public static HttpClientHandler CreatePlatformHealthHandler()
    {
        var handler = new HttpClientHandler { AllowAutoRedirect = false };

        if (File.Exists(K8sCaPath))
        {
            var caCert = LoadCaCert(K8sCaPath);
            if (caCert is not null)
            {
                handler.ServerCertificateCustomValidationCallback =
                    (_, cert, chain, errors) => ValidateAgainstCa(cert, chain, errors, caCert);
            }
        }
        // Most platform-health targets use HTTP internally (Seq, Prometheus, Grafana, ArgoCD, AWX
        // are all configured with http:// cluster-internal URLs). HTTPS requests fall through to
        // system trust, which covers Let's Encrypt-issued ingress certs.

        return handler;
    }

    private static X509Certificate2? LoadCaCert(string path)
    {
        try
        {
            return new X509Certificate2(path);
        }
        catch (Exception ex)
        {
            // Malformed or unreadable CA cert — fall back to system trust.
            // Write to stderr so the degradation is visible in container logs even before
            // the application logger is fully initialised.
            Console.Error.WriteLine(
                $"[GatewayHttpHandlers] WARNING: Failed to load K8s CA cert from '{path}': {ex.Message}. " +
                "Falling back to system trust store — TLS validation may accept unexpected certificates.");
            return null;
        }
    }

    private static bool ValidateAgainstCa(
        X509Certificate2? cert,
        X509Chain? chain,
        SslPolicyErrors errors,
        X509Certificate2 caCert)
    {
        if (errors == SslPolicyErrors.None) return true;
        if (cert is null || chain is null) return false;

        // Rebuild the chain with our custom CA as the only trusted root.
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(caCert);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;

        return chain.Build(cert);
    }
}

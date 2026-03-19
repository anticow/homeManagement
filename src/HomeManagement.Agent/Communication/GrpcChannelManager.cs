using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using Grpc.Net.Client;
using HomeManagement.Agent.Configuration;
using HomeManagement.Agent.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HomeManagement.Agent.Communication;

/// <summary>
/// Manages the gRPC channel to the control server with mTLS credentials.
/// Creates and disposes channels on connect/reconnect cycles.
/// </summary>
public sealed class GrpcChannelManager : IDisposable
{
    private readonly AgentConfiguration _config;
    private readonly CertificateLoader _certLoader;
    private readonly ILogger<GrpcChannelManager> _logger;
    private GrpcChannel? _channel;
    private readonly object _lock = new();

    public GrpcChannelManager(
        IOptions<AgentConfiguration> config,
        CertificateLoader certLoader,
        ILogger<GrpcChannelManager> logger)
    {
        _config = config.Value;
        _certLoader = certLoader;
        _logger = logger;
    }

    /// <summary>
    /// Creates a new gRPC channel with mTLS. Safely builds the new channel before
    /// disposing the old one to prevent a disposed-channel race condition.
    /// </summary>
    public GrpcChannel CreateChannel()
    {
        lock (_lock)
        {
            // Build new channel fully BEFORE disposing the old one.
            // If cert loading or validation throws, _channel remains usable.
            var agentCert = _certLoader.LoadAgentCertificate();
            var caCert = _certLoader.LoadCaCertificate();

            if (!_certLoader.ValidateChain(agentCert, caCert))
                throw new InvalidOperationException("Agent certificate chain validation failed.");

            var handler = new SocketsHttpHandler
            {
                SslOptions = new SslClientAuthenticationOptions
                {
                    ClientCertificates = [agentCert],
                    RemoteCertificateValidationCallback = (_, cert, chain, errors) =>
                        ValidateServerCertificate(cert, chain, errors, caCert)
                },
                PooledConnectionIdleTimeout = Timeout.InfiniteTimeSpan,
                KeepAlivePingDelay = TimeSpan.FromSeconds(20),
                KeepAlivePingTimeout = TimeSpan.FromSeconds(10),
                EnableMultipleHttp2Connections = true
            };

            var address = $"https://{_config.ControlServer}";
            var newChannel = GrpcChannel.ForAddress(address, new GrpcChannelOptions { HttpHandler = handler });

            // Only now dispose the old channel — new one is fully constructed
            var old = _channel;
            _channel = newChannel;
            old?.Dispose();

            _logger.LogInformation("gRPC channel created to {Address}", address);
            return _channel;
        }
    }

    public GrpcChannel GetChannel()
    {
        lock (_lock)
        {
            return _channel ?? throw new InvalidOperationException("Channel not created. Call CreateChannel() first.");
        }
    }

    private bool ValidateServerCertificate(
        X509Certificate? cert,
        X509Chain? chain,
        SslPolicyErrors errors,
        X509Certificate2 caCert)
    {
        if (cert is null)
        {
            _logger.LogWarning("Server did not present a certificate");
            return false;
        }

        var serverCert = new X509Certificate2(cert);
        var isValid = _certLoader.ValidateChain(serverCert, caCert);

        if (!isValid)
            _logger.LogWarning("Server certificate validation failed: {Errors}", errors);

        return isValid;
    }

    public void Dispose()
    {
        _channel?.Dispose();
    }
}

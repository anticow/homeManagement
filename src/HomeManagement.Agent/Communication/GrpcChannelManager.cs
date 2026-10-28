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
    private readonly List<X509Certificate2> _channelCertificates = [];
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
    /// Creates a new gRPC channel. Uses mTLS when UseTls is true (production),
    /// or plain HTTP/2 when UseTls is false (local development).
    /// Safely builds the new channel before disposing the old one.
    /// </summary>
    public GrpcChannel CreateChannel()
    {
        lock (_lock)
        {
            GrpcChannel newChannel;
            var certificates = new List<X509Certificate2>();
            var handler = new SocketsHttpHandler
            {
                PooledConnectionIdleTimeout = Timeout.InfiniteTimeSpan,
                KeepAlivePingDelay = TimeSpan.FromSeconds(20),
                KeepAlivePingTimeout = TimeSpan.FromSeconds(10),
                EnableMultipleHttp2Connections = true
            };

            try
            {
                if (_config.UseTls)
                {
                    var sslOptions = new SslClientAuthenticationOptions();
                    if (!string.IsNullOrWhiteSpace(_config.CertPath))
                    {
                        var agentCert = _certLoader.LoadAgentCertificate();
                        certificates.Add(agentCert);
                        using var clientCaCert = _certLoader.LoadCaCertificate();

                        if (!_certLoader.ValidateChain(agentCert, clientCaCert))
                            throw new InvalidOperationException("Agent certificate chain validation failed.");

                        sslOptions.ClientCertificates = [agentCert];
                    }

                    if (!string.IsNullOrWhiteSpace(_config.ServerCaCertPath)
                        || (!string.IsNullOrWhiteSpace(_config.CertPath) && !string.IsNullOrWhiteSpace(_config.CaCertPath)))
                    {
                        _logger.LogInformation(
                            "Server certificate trust anchor path resolved to {ServerCaCertPath}",
                            string.IsNullOrWhiteSpace(_config.ServerCaCertPath)
                                ? _config.CaCertPath
                                : _config.ServerCaCertPath);

                        var serverCaCert = _certLoader.LoadServerCaCertificate();
                        certificates.Add(serverCaCert);
                        sslOptions.RemoteCertificateValidationCallback = (_, cert, chain, errors) =>
                            ValidateServerCertificate(cert, chain, errors, serverCaCert);
                    }

                    handler.SslOptions = sslOptions;
                }
                else
                {
                    _logger.LogWarning("TLS disabled — connecting to agent gateway over plain HTTP");
                    AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);
                }

                var address = $"{(_config.UseTls ? "https" : "http")}://{_config.ControlServer}";
                newChannel = GrpcChannel.ForAddress(address, new GrpcChannelOptions
                {
                    HttpHandler = handler,
                    DisposeHttpClient = true
                });
            }
            catch
            {
                handler.Dispose();
                foreach (var certificate in certificates)
                    certificate.Dispose();
                throw;
            }

            // Only now dispose the old channel — new one is fully constructed
            var old = _channel;
            _channel = newChannel;
            old?.Dispose();
            foreach (var certificate in _channelCertificates)
                certificate.Dispose();
            _channelCertificates.Clear();
            _channelCertificates.AddRange(certificates);

            _logger.LogInformation("gRPC channel created to {Address}",
                _config.UseTls ? $"https://{_config.ControlServer}" : $"http://{_config.ControlServer}");
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

        using var serverCert = new X509Certificate2(cert);
        var isValid = _certLoader.ValidateChain(serverCert, caCert);

        if (!isValid)
            _logger.LogWarning("Server certificate validation failed: {Errors}", errors);

        return isValid;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _channel?.Dispose();
            _channel = null;
            foreach (var certificate in _channelCertificates)
                certificate.Dispose();
            _channelCertificates.Clear();
        }
    }
}

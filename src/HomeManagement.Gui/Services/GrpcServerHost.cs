using System.Net;
using System.Security.Cryptography.X509Certificates;
using HomeManagement.Transport;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;

namespace HomeManagement.Gui.Services;

/// <summary>
/// Embeds a Kestrel gRPC server inside the desktop GUI process so agents
/// can connect directly to the control plane. Starts on port 9443 with mTLS.
/// </summary>
internal sealed class GrpcServerHost : IDisposable
{
    private WebApplication? _app;

    /// <summary>
    /// Start the gRPC server on a background thread. Shares the AgentGatewayService
    /// singleton from the main DI container so agent connections are visible to the GUI.
    /// </summary>
    public async Task StartAsync(IServiceProvider appServices, string dataDir, CancellationToken ct)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(ConfigureKestrel);
        builder.Services.AddLogging(lb => lb.AddSerilog(dispose: false));

        builder.Services.AddGrpc();

        // Share the AgentGatewayService singleton from the main app container
        var gateway = appServices.GetRequiredService<AgentGatewayService>();
        builder.Services.AddSingleton(gateway);

        _app = builder.Build();
        _app.MapGrpcService<AgentHubService>();

        await _app.StartAsync(ct);
        Log.Information("gRPC control server listening on https://0.0.0.0:9444");
    }

    private static void ConfigureKestrel(KestrelServerOptions options)
    {
        var certPath = Path.GetFullPath("certs/server.pfx");
        var caPath = Path.GetFullPath("certs/ca.crt");

        if (!File.Exists(certPath))
        {
            Log.Warning("Server certificate not found at {Path} — gRPC server will use HTTP/2 without TLS", certPath);
            options.Listen(IPAddress.Any, 9444, lo => lo.Protocols = HttpProtocols.Http2);
            return;
        }

        var serverCert = new X509Certificate2(certPath);
        var caCert = File.Exists(caPath) ? new X509Certificate2(caPath) : null;

        options.Listen(IPAddress.Any, 9444, lo =>
        {
            lo.Protocols = HttpProtocols.Http2;
            lo.UseHttps(https =>
            {
                https.ServerCertificate = serverCert;
                if (caCert is not null)
                {
                    https.ClientCertificateMode = Microsoft.AspNetCore.Server.Kestrel.Https.ClientCertificateMode.RequireCertificate;
                    https.ClientCertificateValidation = (cert, chain, errors) =>
                    {
                        if (chain is null) return false;
                        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                        chain.ChainPolicy.CustomTrustStore.Add(caCert);
                        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                        return chain.Build(cert!);
                    };
                }
            });
        });
    }

    public async Task StopAsync()
    {
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
            _app = null;
        }
    }

    public void Dispose()
    {
        StopAsync().GetAwaiter().GetResult();
    }
}

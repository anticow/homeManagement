using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using FluentAssertions;
using HomeManagement.Agent.Configuration;
using HomeManagement.Agent.Security;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HomeManagement.Agent.Tests.Security;

public sealed class CertificateLoaderTests : IDisposable
{
    private readonly string _tempDirectory;

    public CertificateLoaderTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "hm-agent-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);
    }

    [Fact]
    public void LoadServerCaCertificate_FallsBackToCaCertPath_WhenServerCaCertPathMissing()
    {
        var fallbackPath = CreateCertificateFile("fallback-ca.cer", "CN=Fallback CA");
        var config = new AgentConfiguration
        {
            CaCertPath = fallbackPath,
            ServerCaCertPath = null
        };

        var loader = CreateLoader(config);

        using var certificate = loader.LoadServerCaCertificate();

        certificate.Subject.Should().Contain("CN=Fallback CA");
    }

    [Fact]
    public void LoadServerCaCertificate_UsesDedicatedServerCaCertPath_WhenConfigured()
    {
        var clientCaPath = CreateCertificateFile("client-ca.cer", "CN=Client CA");
        var serverCaPath = CreateCertificateFile("server-ca.cer", "CN=Server CA");
        var config = new AgentConfiguration
        {
            CaCertPath = clientCaPath,
            ServerCaCertPath = serverCaPath
        };

        var loader = CreateLoader(config);

        using var certificate = loader.LoadServerCaCertificate();

        certificate.Subject.Should().Contain("CN=Server CA");
    }

    private static CertificateLoader CreateLoader(AgentConfiguration configuration)
    {
        return new CertificateLoader(Options.Create(configuration), NullLogger<CertificateLoader>.Instance);
    }

    private string CreateCertificateFile(string fileName, string subjectName)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(subjectName, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));

        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        var path = Path.Combine(_tempDirectory, fileName);
        File.WriteAllBytes(path, certificate.Export(X509ContentType.Cert));
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
    }
}

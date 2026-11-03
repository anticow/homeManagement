using Grpc.Core;
using HomeManagement.Agent.Protocol;
using Microsoft.Extensions.Options;

namespace HomeManagement.AgentGateway.Host.Services;

internal static class AgentEnrollmentRegistration
{
    public static void AddGatewayAgentAuthority(this IServiceCollection services, IConfiguration configuration)
    {
        var enabled = configuration.GetValue<bool>("AgentGateway:Enrollment:Enabled");
        services.AddSingleton(new AgentEnrollmentMode(enabled));
        if (!enabled)
        {
            services.AddSingleton<RevokedAgentStore>();
            services.AddSingleton<IRevokedAgentStore>(sp => sp.GetRequiredService<RevokedAgentStore>());
            services.AddSingleton<AgentApiKeyValidator>();
            services.AddSingleton<IAgentApiKeyValidator>(sp => sp.GetRequiredService<AgentApiKeyValidator>());
            services.AddSingleton<StandaloneAgentGatewayService>();
            return;
        }
        services.AddSingleton(sp =>
        {
            var options = sp.GetRequiredService<IOptions<AgentGatewayHostOptions>>().Value;
            var path = options.Enrollment.ProvisioningKeyPath;
            if (string.IsNullOrWhiteSpace(path) || new FileInfo(path).Length is < 1 or > 128)
                throw new InvalidDataException("A mounted provisioning key file is required.");
            var key = File.ReadAllText(path).TrimEnd('\r', '\n');
            if (!AgentEnrollmentRegistry.IsStrongKey(key))
                throw new InvalidDataException("The provisioning key must be canonical Base64 with 32 random bytes.");
            return new AgentProvisioningAuthority(key);
        });
        services.AddSingleton(sp =>
        {
            var options = sp.GetRequiredService<IOptions<AgentGatewayHostOptions>>().Value;
            var provisioning = sp.GetRequiredService<AgentProvisioningAuthority>();
            return new AgentEnrollmentRegistry(
                new EncryptedAgentEnrollmentStore(options.Enrollment.Directory, options.Enrollment.WrappingKeyPath),
                options.ApiKey, provisioning.Key);
        });
        services.AddSingleton<IAgentApiKeyValidator, RegistryOnlyAdmission>();
        services.AddSingleton(sp => new StandaloneAgentGatewayService(
            sp.GetRequiredService<ILogger<StandaloneAgentGatewayService>>(), new InactiveLegacyRevocations(),
            sp.GetRequiredService<AgentEnrollmentRegistry>()));
        services.AddHealthChecks().AddCheck<AgentEnrollmentRegistry>("agent-enrollment", tags: ["readiness"]);
    }

    public static void InitializeGatewayAgentAuthority(this WebApplication app)
    {
        if (app.Services.GetRequiredService<AgentEnrollmentMode>().Enabled)
        {
            _ = app.Services.GetRequiredService<AgentEnrollmentRegistry>();
            _ = app.Services.GetRequiredService<StandaloneAgentGatewayService>();
        }
    }

    private sealed class RegistryOnlyAdmission : IAgentApiKeyValidator
    {
        public void ValidateOrThrow(ServerCallContext context, Handshake handshake) =>
            throw new InvalidOperationException("Registry admission must use the session coordinator.");
    }

    private sealed class InactiveLegacyRevocations : IRevokedAgentStore
    {
        public bool IsRevoked(string agentId) => throw new InvalidOperationException("Legacy authority is disabled.");
        public void Revoke(string agentId, string reason) => throw new InvalidOperationException("Legacy authority is disabled.");
        public void Reinstate(string agentId) => throw new InvalidOperationException("Legacy authority is disabled.");
        public IReadOnlyList<RevokedAgentEntry> GetAll() => throw new InvalidOperationException("Legacy authority is disabled.");
    }
}

internal sealed record AgentEnrollmentMode(bool Enabled);

internal sealed class AgentProvisioningAuthority(string key)
{
    internal string Key { get; } = key;
}

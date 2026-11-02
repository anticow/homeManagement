using Grpc.Core;
using HomeManagement.Agent.Protocol;

namespace HomeManagement.AgentGateway.Host.Services;

public interface IAgentApiKeyValidator
{
    bool SupportsManagedServiceControl(string agentId) => false;
    void ValidateOrThrow(ServerCallContext context, Handshake handshake);
}

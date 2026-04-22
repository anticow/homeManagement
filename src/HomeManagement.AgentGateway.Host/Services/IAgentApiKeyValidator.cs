using Grpc.Core;
using HomeManagement.Agent.Protocol;

namespace HomeManagement.AgentGateway.Host.Services;

public interface IAgentApiKeyValidator
{
    void ValidateOrThrow(ServerCallContext context, Handshake handshake);
}

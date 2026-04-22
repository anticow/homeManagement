FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
WORKDIR /app

# Install Azure CLI for KeyVault resolution
RUN apt-get update && apt-get install -y --no-install-recommends \
    azure-cli \
    jq \
    ca-certificates \
    && rm -rf /var/lib/apt/lists/*

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props ./
COPY src/HomeManagement.Abstractions/HomeManagement.Abstractions.csproj src/HomeManagement.Abstractions/
COPY src/HomeManagement.Agent/HomeManagement.Agent.csproj src/HomeManagement.Agent/
COPY src/HomeManagement.Agent/Protos/ src/HomeManagement.Agent/Protos/
RUN dotnet restore src/HomeManagement.Agent/HomeManagement.Agent.csproj

COPY src/ src/
RUN dotnet publish src/HomeManagement.Agent/HomeManagement.Agent.csproj \
    -c Release -o /app/publish --no-restore

FROM base AS final
WORKDIR /app
COPY --from=build /app/publish .

# Copy boot script for KeyVault configuration resolution
COPY scripts/resolve-keyvault-config.sh /app/scripts/
RUN chmod +x /app/scripts/resolve-keyvault-config.sh

# Copy configuration template
COPY deploy/config/hm-agent.json.template /app/config/

# Pre-startup: Resolve configuration from KeyVault
# This runs before the agent starts, populating secrets from Azure KeyVault
# using Managed Identity (in AKS/ACI) or Service Principal (local/dev)
ENTRYPOINT ["/bin/bash", "-c", \
    "/app/scripts/resolve-keyvault-config.sh && \
    exec dotnet HomeManagement.Agent.dll"]

# Environment variables (set via deployment):
# - AZURE_VAULT_NAME: Azure KeyVault name (default: kv-antic0wa777429445286)
# - AGENT_ID: Agent identifier (required)
# - AZURE_CLIENT_ID: Service Principal client ID (optional)
# - AZURE_CLIENT_SECRET: Service Principal client secret (optional)
# - AZURE_TENANT_ID: Azure tenant ID (optional if using Service Principal)


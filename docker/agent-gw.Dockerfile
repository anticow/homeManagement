FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
RUN apt-get update && apt-get install -y --no-install-recommends curl && rm -rf /var/lib/apt/lists/*
WORKDIR /app
EXPOSE 9444

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props ./
COPY src/HomeManagement.Abstractions/HomeManagement.Abstractions.csproj src/HomeManagement.Abstractions/
COPY src/HomeManagement.Data/HomeManagement.Data.csproj src/HomeManagement.Data/
COPY src/HomeManagement.Core/HomeManagement.Core.csproj src/HomeManagement.Core/
COPY src/HomeManagement.Agent/Protos/ src/HomeManagement.Agent/Protos/
COPY src/HomeManagement.AgentGateway.Host/HomeManagement.AgentGateway.Host.csproj src/HomeManagement.AgentGateway.Host/
RUN dotnet restore src/HomeManagement.AgentGateway.Host/HomeManagement.AgentGateway.Host.csproj

COPY src/ src/
RUN dotnet publish src/HomeManagement.AgentGateway.Host/HomeManagement.AgentGateway.Host.csproj \
    -c Release -o /app/publish --no-restore

FROM base AS final
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "HomeManagement.AgentGateway.Host.dll"]

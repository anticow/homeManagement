FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
RUN apt-get update && apt-get install -y --no-install-recommends curl && rm -rf /var/lib/apt/lists/*
WORKDIR /app
EXPOSE 8082

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props ./
COPY src/HomeManagement.Abstractions/HomeManagement.Abstractions.csproj src/HomeManagement.Abstractions/
COPY src/HomeManagement.Data/HomeManagement.Data.csproj src/HomeManagement.Data/
COPY src/HomeManagement.Data.SqlServer/HomeManagement.Data.SqlServer.csproj src/HomeManagement.Data.SqlServer/
COPY src/HomeManagement.Auth/HomeManagement.Auth.csproj src/HomeManagement.Auth/
COPY src/HomeManagement.Vault/HomeManagement.Vault.csproj src/HomeManagement.Vault/
COPY src/HomeManagement.Transport/HomeManagement.Transport.csproj src/HomeManagement.Transport/
COPY src/HomeManagement.Patching/HomeManagement.Patching.csproj src/HomeManagement.Patching/
COPY src/HomeManagement.Services/HomeManagement.Services.csproj src/HomeManagement.Services/
COPY src/HomeManagement.Inventory/HomeManagement.Inventory.csproj src/HomeManagement.Inventory/
COPY src/HomeManagement.Auditing/HomeManagement.Auditing.csproj src/HomeManagement.Auditing/
COPY src/HomeManagement.Orchestration/HomeManagement.Orchestration.csproj src/HomeManagement.Orchestration/
COPY src/HomeManagement.Core/HomeManagement.Core.csproj src/HomeManagement.Core/
COPY src/HomeManagement.Broker.Host/HomeManagement.Broker.Host.csproj src/HomeManagement.Broker.Host/
RUN dotnet restore src/HomeManagement.Broker.Host/HomeManagement.Broker.Host.csproj

COPY src/ src/
RUN dotnet publish src/HomeManagement.Broker.Host/HomeManagement.Broker.Host.csproj \
    -c Release -o /app/publish --no-restore

FROM base AS final
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "HomeManagement.Broker.Host.dll"]

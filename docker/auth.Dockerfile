FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
RUN apt-get update && apt-get install -y --no-install-recommends curl && rm -rf /var/lib/apt/lists/*
WORKDIR /app
EXPOSE 8083

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props ./
COPY src/HomeManagement.Abstractions/HomeManagement.Abstractions.csproj src/HomeManagement.Abstractions/
COPY src/HomeManagement.Data/HomeManagement.Data.csproj src/HomeManagement.Data/
COPY src/HomeManagement.Data.SqlServer/HomeManagement.Data.SqlServer.csproj src/HomeManagement.Data.SqlServer/
COPY src/HomeManagement.Core/HomeManagement.Core.csproj src/HomeManagement.Core/
COPY src/HomeManagement.Auth/HomeManagement.Auth.csproj src/HomeManagement.Auth/
COPY src/HomeManagement.Auth.Host/HomeManagement.Auth.Host.csproj src/HomeManagement.Auth.Host/
RUN dotnet restore src/HomeManagement.Auth.Host/HomeManagement.Auth.Host.csproj

COPY src/ src/
RUN dotnet publish src/HomeManagement.Auth.Host/HomeManagement.Auth.Host.csproj \
    -c Release -o /app/publish --no-restore

FROM base AS final
RUN adduser --disabled-password --gecos "" appuser && \
    mkdir -p /app/logs && chown appuser:appuser /app/logs
COPY --from=build /app/publish .
USER appuser
ENTRYPOINT ["dotnet", "HomeManagement.Auth.Host.dll"]

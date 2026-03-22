FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
WORKDIR /app

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
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "HomeManagement.Agent.dll"]

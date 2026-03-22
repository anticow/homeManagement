FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
WORKDIR /app
EXPOSE 8081

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props ./
COPY src/HomeManagement.Abstractions/HomeManagement.Abstractions.csproj src/HomeManagement.Abstractions/
COPY src/HomeManagement.Auth/HomeManagement.Auth.csproj src/HomeManagement.Auth/
COPY src/HomeManagement.Gateway/HomeManagement.Gateway.csproj src/HomeManagement.Gateway/
RUN dotnet restore src/HomeManagement.Gateway/HomeManagement.Gateway.csproj

COPY src/ src/
RUN dotnet publish src/HomeManagement.Gateway/HomeManagement.Gateway.csproj \
    -c Release -o /app/publish --no-restore

FROM base AS final
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "HomeManagement.Gateway.dll"]

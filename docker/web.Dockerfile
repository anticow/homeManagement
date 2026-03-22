FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
WORKDIR /app
EXPOSE 8080

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props ./
COPY src/HomeManagement.Abstractions/HomeManagement.Abstractions.csproj src/HomeManagement.Abstractions/
COPY src/HomeManagement.Auth/HomeManagement.Auth.csproj src/HomeManagement.Auth/
COPY src/HomeManagement.Web/HomeManagement.Web.csproj src/HomeManagement.Web/
RUN dotnet restore src/HomeManagement.Web/HomeManagement.Web.csproj

COPY src/ src/
RUN dotnet publish src/HomeManagement.Web/HomeManagement.Web.csproj \
    -c Release -o /app/publish --no-restore

FROM base AS final
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "HomeManagement.Web.dll"]

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
RUN apt-get update && apt-get install -y --no-install-recommends curl && rm -rf /var/lib/apt/lists/*
WORKDIR /app
EXPOSE 8080

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG VERSION=0.0.0
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props ./
COPY src/HomeManagement.Abstractions/HomeManagement.Abstractions.csproj src/HomeManagement.Abstractions/
COPY src/HomeManagement.Data/HomeManagement.Data.csproj src/HomeManagement.Data/
COPY src/HomeManagement.Core/HomeManagement.Core.csproj src/HomeManagement.Core/
COPY src/HomeManagement.Auth/HomeManagement.Auth.csproj src/HomeManagement.Auth/
COPY src/HomeManagement.Web/HomeManagement.Web.csproj src/HomeManagement.Web/
RUN dotnet restore src/HomeManagement.Web/HomeManagement.Web.csproj

COPY src/ src/
RUN dotnet publish src/HomeManagement.Web/HomeManagement.Web.csproj \
    -c Release -o /app/publish --no-restore -p:Version=$VERSION

FROM base AS final
RUN mkdir -p /app/logs && chown "$APP_UID:$APP_UID" /app/logs
COPY --from=build /app/publish .
ENV HOME=/home/app
USER $APP_UID
ENTRYPOINT ["dotnet", "HomeManagement.Web.dll"]

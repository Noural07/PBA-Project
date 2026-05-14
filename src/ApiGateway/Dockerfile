# syntax=docker/dockerfile:1.7
# Multi-stage build for the ApiGateway (YARP). Build context: repository root.

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Restore med blot csproj-filerne for at udnytte Docker layer cache.
COPY Directory.Build.props ./
COPY src/Shared/Shared.csproj            src/Shared/
COPY src/ApiGateway/ApiGateway.csproj    src/ApiGateway/
RUN dotnet restore src/ApiGateway/ApiGateway.csproj

# Kopier resterende kildekode og publicer en framework-dependent runtime.
COPY src/Shared/      src/Shared/
COPY src/ApiGateway/  src/ApiGateway/
RUN dotnet publish src/ApiGateway/ApiGateway.csproj \
    -c Release \
    -o /app/publish \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_RUNNING_IN_CONTAINER=true \
    DOTNET_USE_POLLING_FILE_WATCHER=true
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "ApiGateway.dll"]

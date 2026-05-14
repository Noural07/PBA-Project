# syntax=docker/dockerfile:1.7
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

COPY Directory.Build.props ./
COPY src/Shared/Shared.csproj                       src/Shared/
COPY src/IngestionService/IngestionService.csproj   src/IngestionService/
RUN dotnet restore src/IngestionService/IngestionService.csproj

COPY src/Shared/            src/Shared/
COPY src/IngestionService/  src/IngestionService/
RUN dotnet publish src/IngestionService/IngestionService.csproj \
    -c Release \
    -o /app/publish \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_RUNNING_IN_CONTAINER=true
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "IngestionService.dll"]

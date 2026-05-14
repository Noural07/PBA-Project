# syntax=docker/dockerfile:1.7
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

COPY Directory.Build.props ./
COPY src/Shared/Shared.csproj                       src/Shared/
COPY src/AnalyzerService/AnalyzerService.csproj     src/AnalyzerService/
RUN dotnet restore src/AnalyzerService/AnalyzerService.csproj

COPY src/Shared/            src/Shared/
COPY src/AnalyzerService/   src/AnalyzerService/
RUN dotnet publish src/AnalyzerService/AnalyzerService.csproj \
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
ENTRYPOINT ["dotnet", "AnalyzerService.dll"]

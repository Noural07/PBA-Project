using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Pba.Shared.HealthChecks;

/// <summary>
/// Registrerer en standardiseret palette af health checks for samtlige
/// microservices. Dækker den interne procestilstand (self), den relationelle
/// database (PostgreSQL via Npgsql, betinget) og message broker'en
/// (RabbitMQ AMQP-port).
/// </summary>
/// <remarks>
/// <para>
/// <b>Database-per-Service.</b> I overensstemmelse med bounded-context-
/// princippet er Postgres-health-checket gjort betinget. Tjenester der ikke
/// ejer et datalag (typisk reverse-proxies eller rent reaktive event-
/// konsumenter som ApiGateway, IngestionService og AiService) skal kalde
/// metoden med <paramref name="requirePostgres"/> sat til <c>false</c>.
/// Derved undgås en kunstig kobling: en utilgængelig database kan ikke
/// længere markere en stateless tjeneste som "Unhealthy".
/// </para>
/// <para>
/// Mønstret følger Microsoft's anbefaling om at health checks afspejler
/// tjenestens faktiske afhængigheder; ekstra checks anses som false-positive-
/// generatorer og bør derfor undgås. Adskillelsen letter samtidig
/// Database-per-Service-overgangen ved kun at lade ejer-tjenesterne ramme
/// deres egen logiske database.
/// </para>
/// </remarks>
public static class HealthChecksRegistration
{
    // Tag-arrays er extracted som static readonly for at tilfredsstille
    // analyzer-reglen CA1861. Tag'sene anvendes både til Gatus-filtrering og
    // til Kubernetes-style liveness/readiness-probes (jf. /health/live og
    // /health/ready i hver service).
    private static readonly string[] LiveTags = ["live"];
    private static readonly string[] InfraReadyTags = ["infra", "ready"];

    /// <summary>
    /// Registrerer platformens standardiserede health checks.
    /// </summary>
    /// <param name="services">DI-container.</param>
    /// <param name="configuration">Applikationskonfiguration.</param>
    /// <param name="requirePostgres">
    /// Hvorvidt servicen ejer et Postgres-datalag og dermed skal ramme
    /// databasen som del af sit readiness-check. Default er <c>true</c>
    /// for at bevare bagudkompatibilitet, men tjenester uden DbContext
    /// SKAL sætte den til <c>false</c>, jf. Database-per-Service-mønsteret.
    /// </param>
    public static IServiceCollection AddPlatformHealthChecks(
        this IServiceCollection services,
        IConfiguration configuration,
        bool requirePostgres = true)
    {
        var rabbitHost = configuration["RabbitMq:Host"] ?? "localhost";
        var rabbitPort = int.TryParse(configuration["RabbitMq:Port"], out var port) ? port : 5672;

        var healthChecksBuilder = services.AddHealthChecks()
            .AddCheck("self", () => HealthCheckResult.Healthy("Service process is running."), tags: LiveTags)
            .AddCheck(
                name: "rabbitmq",
                instance: new RabbitMqTcpHealthCheck(rabbitHost, rabbitPort),
                failureStatus: HealthStatus.Unhealthy,
                tags: InfraReadyTags);

        if (requirePostgres)
        {
            var postgresConnection = configuration.GetConnectionString("Postgres")
                ?? configuration["Postgres:ConnectionString"]
                ?? "Host=localhost;Port=5432;Database=pba;Username=pba;Password=pba";

            healthChecksBuilder.AddNpgSql(
                connectionString: postgresConnection,
                name: "postgres",
                failureStatus: HealthStatus.Unhealthy,
                tags: InfraReadyTags);
        }

        return services;
    }
}

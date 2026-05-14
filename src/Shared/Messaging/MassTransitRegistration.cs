using System;
using MassTransit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Pba.Shared.Messaging;

/// <summary>
/// Centraliseret opsætning af MassTransit ovenpå RabbitMQ for samtlige
/// microservices. Sikrer ensartet broker-konfiguration (host, credentials,
/// retry-politik) og lader hver enkelt service udelukkende fokusere på
/// at registrere sine consumers og publicerede kontrakter.
/// </summary>
public static class MassTransitRegistration
{
    /// <summary>
    /// Registrerer MassTransit med RabbitMQ-transporten. Værdier hentes
    /// fra konfigurationen under <c>RabbitMq:*</c> og kan overskrives via
    /// miljøvariablene <c>RABBITMQ_HOST</c>, <c>RABBITMQ_PORT</c>,
    /// <c>RABBITMQ_USER</c> og <c>RABBITMQ_PASSWORD</c>.
    /// </summary>
    /// <param name="services">DI-container.</param>
    /// <param name="configuration">Applikationskonfiguration.</param>
    /// <param name="configure">
    /// Hook hvor servicens egne consumers og endpoint-konventioner registreres.
    /// </param>
    public static IServiceCollection AddPlatformMassTransit(
        this IServiceCollection services,
        IConfiguration configuration,
        Action<IBusRegistrationConfigurator>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var host = configuration["RabbitMq:Host"]
            ?? Environment.GetEnvironmentVariable("RABBITMQ_HOST")
            ?? "localhost";

        var portRaw = configuration["RabbitMq:Port"]
            ?? Environment.GetEnvironmentVariable("RABBITMQ_PORT");
        var port = ushort.TryParse(portRaw, out var parsed) ? parsed : (ushort)5672;

        var user = configuration["RabbitMq:User"]
            ?? Environment.GetEnvironmentVariable("RABBITMQ_USER")
            ?? "guest";

        var password = configuration["RabbitMq:Password"]
            ?? Environment.GetEnvironmentVariable("RABBITMQ_PASSWORD")
            ?? "guest";

        var virtualHost = configuration["RabbitMq:VirtualHost"]
            ?? Environment.GetEnvironmentVariable("RABBITMQ_VHOST")
            ?? "/";

        services.AddMassTransit(busConfigurator =>
        {
            // Standardiseret kø-navngivning på tværs af platformen:
            // pba.<message-name> (kebab-case). Letter aflæsning i RabbitMQ
            // management-UI og giver forudsigelige bindings.
            busConfigurator.SetKebabCaseEndpointNameFormatter();

            // Service-specifik registrering (consumers, sagas mv.).
            configure?.Invoke(busConfigurator);

            busConfigurator.UsingRabbitMq((context, cfg) =>
            {
                cfg.Host(host, port, virtualHost, h =>
                {
                    h.Username(user);
                    h.Password(password);
                });

                // Idiomatisk MassTransit-retry: tre forsøg med
                // eksponentielt voksende interval. Forhindrer at
                // forbigående netværksfejl medfører tab af events,
                // mens persistente fejl stadig sendes til broker'ens
                // dead-letter-mekanisme efter de tre forsøg.
                cfg.UseMessageRetry(r => r.Exponential(
                    retryLimit: 3,
                    minInterval: TimeSpan.FromSeconds(1),
                    maxInterval: TimeSpan.FromSeconds(15),
                    intervalDelta: TimeSpan.FromSeconds(2)));

                cfg.ConfigureEndpoints(context);
            });
        });

        return services;
    }
}

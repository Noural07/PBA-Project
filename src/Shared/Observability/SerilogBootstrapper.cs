using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Serilog;
using Serilog.Formatting.Compact;
using Serilog.Sinks.Grafana.Loki;

namespace Pba.Shared.Observability;

/// <summary>
/// Centraliseret Serilog-opsætning anvendt af samtlige microservices i platformen.
/// Skriver strukturerede JSON-linjer til både konsol og Loki, og beriger hver
/// linje med servicens navn, host og miljø, så logs kan filtreres i Grafana via
/// labels.
/// </summary>
public static class SerilogBootstrapper
{
    // Felterne nedenfor er extracted som static readonly for at tilfredsstille
    // analyzer-reglen CA1861 (Prefer 'static readonly' fields over constant
    // array arguments). Værdierne læses kun ved start-up, men reglen håndhæves
    // for at fange utilsigtede allokeringer i hot paths.
    private static readonly string[] LokiPropertiesAsLabels = ["level"];

    /// <summary>
    /// Opsætter en bootstrap-logger inden host'en bygges, så fejl under
    /// opstart fanges. Skal altid suppleres med et efterfølgende kald til
    /// <see cref="UseStandardSerilog"/>.
    /// </summary>
    public static void ConfigureBootstrapLogger(string serviceName)
    {
        Log.Logger = new LoggerConfiguration()
            .Enrich.FromLogContext()
            .Enrich.WithProperty("service", serviceName)
            .WriteTo.Console(new CompactJsonFormatter())
            .CreateBootstrapLogger();
    }

    /// <summary>
    /// Konfigurerer Serilog som primær logger for værts-builderen og
    /// tilkobler både konsolsink (CLEF JSON) og Loki-sink. Loki-URL'en
    /// hentes fra konfigurationen under nøglen <c>Loki:Url</c> (kan
    /// overskrives via miljøvariablen <c>LOKI__URL</c>). Hvis URL'en er tom
    /// springes Loki-sinken over, så lokal udvikling uden Loki ikke fejler.
    /// </summary>
    public static WebApplicationBuilder UseStandardSerilog(
        this WebApplicationBuilder builder,
        string serviceName)
    {
        builder.Host.UseSerilog((context, services, loggerConfig) =>
        {
            loggerConfig
                .ReadFrom.Configuration(context.Configuration)
                .ReadFrom.Services(services)
                .Enrich.FromLogContext()
                .Enrich.WithMachineName()
                .Enrich.WithEnvironmentName()
                .Enrich.WithProperty("service", serviceName)
                .WriteTo.Console(new CompactJsonFormatter());

            var lokiUrl = context.Configuration["Loki:Url"];
            if (!string.IsNullOrWhiteSpace(lokiUrl))
            {
                var labels = new[]
                {
                    new LokiLabel { Key = "service", Value = serviceName },
                    new LokiLabel { Key = "environment", Value = context.HostingEnvironment.EnvironmentName }
                };

                loggerConfig.WriteTo.GrafanaLoki(
                    uri: lokiUrl,
                    labels: labels,
                    propertiesAsLabels: LokiPropertiesAsLabels);
            }
        });

        return builder;
    }
}

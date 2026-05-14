using HealthChecks.UI.Client;
using IngestionService.Endpoints;
using IngestionService.Pipeline;
using IngestionService.Trendlog;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Pba.Shared.HealthChecks;
using Pba.Shared.Messaging;
using Pba.Shared.Observability;
using Serilog;

const string ServiceName = "ingestion-service";

SerilogBootstrapper.ConfigureBootstrapLogger(ServiceName);

try
{
    Log.Information("Bootstrapping {Service}", ServiceName);

    var builder = WebApplication.CreateBuilder(args);
    builder.UseStandardSerilog(ServiceName);

    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen(options =>
    {
        options.SwaggerDoc("v1", new Microsoft.OpenApi.Models.OpenApiInfo
        {
            Title = $"{ServiceName} API",
            Version = "v1",
            Description = "Ingestion endpoint for Trendlog measurements (Phase 3 – data pipeline)."
        });
    });

    // Database-per-Service: IngestionService persisterer ikke noget i
    // Postgres (al state lever som events på RabbitMQ), så readiness-checket
    // skal ikke ramme databasen — det ville være en kunstig kobling.
    builder.Services.AddPlatformHealthChecks(builder.Configuration, requirePostgres: false);

    // Phase 3 – Pipeline & messaging.
    builder.Services.AddSingleton<IMeasurementNormalizer, MeasurementNormalizer>();
    builder.Services.AddScoped<IngestionPublisher>();
    builder.Services.AddPlatformMassTransit(builder.Configuration);

    // Fase A – Live Trendlog-integration (typed HttpClient + options-validering).
    builder.Services.AddTrendlogIntegration(builder.Configuration);

    var app = builder.Build();

    app.UseSerilogRequestLogging();

    if (app.Environment.IsDevelopment())
    {
        app.UseSwagger();
        app.UseSwaggerUI();
    }

    app.MapHealthChecks("/health", new HealthCheckOptions
    {
        ResponseWriter = HealthEndpointWriter.WriteJsonResponse
    });

    app.MapHealthChecks("/health/live", new HealthCheckOptions
    {
        Predicate = registration => registration.Tags.Contains("live"),
        ResponseWriter = UIResponseWriter.WriteHealthCheckUIResponse
    });

    app.MapHealthChecks("/health/ready", new HealthCheckOptions
    {
        Predicate = registration => registration.Tags.Contains("ready"),
        ResponseWriter = UIResponseWriter.WriteHealthCheckUIResponse
    });

    app.MapGet("/", () => Results.Ok(new
    {
        service = ServiceName,
        status = "running",
        timestamp = DateTimeOffset.UtcNow
    }))
    .WithName("Root")
    .WithOpenApi();

    app.MapIngestionEndpoints();
    app.MapTrendlogLiveEndpoints();

    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "{Service} terminated unexpectedly during startup", ServiceName);
    throw;
}
finally
{
    Log.CloseAndFlush();
}

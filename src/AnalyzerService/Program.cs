using AnalyzerService.Consumers;
using AnalyzerService.Endpoints;
using AnalyzerService.Persistence;
using AnalyzerService.Rules;
using HealthChecks.UI.Client;
using MassTransit;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Pba.Shared.HealthChecks;
using Pba.Shared.Messaging;
using Pba.Shared.Observability;
using Serilog;

const string ServiceName = "analyzer-service";

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
            Description = "Analyzer service that persists measurements and applies critical-event rules (Phase 3)."
        });
    });

    builder.Services.AddPlatformHealthChecks(builder.Configuration);

    // persistens.
    var connectionString = builder.Configuration.GetConnectionString("Postgres")
        ?? "Host=postgres;Port=5432;Database=pba;Username=pba;Password=pba";

    builder.Services.AddDbContext<AnalyzerDbContext>(options =>
        options.UseNpgsql(connectionString, npg => npg.EnableRetryOnFailure(maxRetryCount: 5)));

    builder.Services.AddHostedService<DatabaseInitializer>();

    // kritikalitetsregler.
    builder.Services.AddOptions<CriticalRuleOptions>()
        .Bind(builder.Configuration.GetSection(CriticalRuleOptions.SectionName));
    builder.Services.AddSingleton<CriticalRuleEvaluator>();

    // messaging.
    builder.Services.AddPlatformMassTransit(builder.Configuration, mt =>
    {
        mt.AddConsumer<MeasurementReceivedConsumer>();
    });

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

    app.MapAnalyzerEndpoints();

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

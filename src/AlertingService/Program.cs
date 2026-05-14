using System;
using AlertingService.Consumers;
using AlertingService.Domain;
using AlertingService.Endpoints;
using AlertingService.Persistence;
using HealthChecks.UI.Client;
using MassTransit;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Pba.Shared.HealthChecks;
using Pba.Shared.Messaging;
using Pba.Shared.Observability;
using Serilog;

const string ServiceName = "alerting-service";
const string CorsPolicyName = "AlertFrontend";

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
            Description = "Alert consolidation service exposing an SSE stream consumed by the AlertFeed frontend (Phase 4 + Fase C audit-persistence)."
        });
    });

    builder.Services.AddPlatformHealthChecks(builder.Configuration);

    // ----------------------------------------------------------------------
    // Phase 4 – CORS.
    // Læser ALERTING_FRONTEND_ORIGIN først fra miljøvariabler (foretrukne for
    // konfiguration af hemmeligheder/topologi i container-miljøer) og dernæst
    // fra appsettings (Cors:AllowedOrigins). Default er Vite's dev-port, så
    // git-clone + dotnet run virker uden ekstra opsætning.
    // ----------------------------------------------------------------------
    var allowedOrigin = Environment.GetEnvironmentVariable("ALERTING_FRONTEND_ORIGIN")
        ?? builder.Configuration["Cors:AllowedOrigins"]
        ?? "http://localhost:5173";

    Log.Information("CORS-allowlist for SSE-streamen sat til {Origin}", allowedOrigin);

    builder.Services.AddCors(options =>
    {
        options.AddPolicy(CorsPolicyName, policy =>
        {
            policy.WithOrigins(allowedOrigin)
                  .WithMethods("GET")
                  .WithHeaders("Accept", "Cache-Control")
                  .DisallowCredentials();
        });
    });

    // ----------------------------------------------------------------------
    // Fase C – persistens af AI-klassifikationer.
    //
    // AlertingService overtager rollen som langtidsholdbar audit-log for
    // StopReasonClassified-events. Persisteringen er additiv til den
    // eksisterende in-memory ring-buffer og påvirker ikke SSE-streamen:
    // hvis Postgres er midlertidigt utilgængeligt, retry'er MassTransit-
    // pipelinen og frontend'en modtager alarmen uændret.
    // ----------------------------------------------------------------------
    var connectionString = builder.Configuration.GetConnectionString("Postgres")
        ?? "Host=postgres;Port=5432;Database=pba;Username=pba;Password=pba";

    builder.Services.AddDbContext<AlertingDbContext>(options =>
        options.UseNpgsql(connectionString, npg => npg.EnableRetryOnFailure(maxRetryCount: 5)));

    builder.Services.AddHostedService<DatabaseInitializer>();

    // ----------------------------------------------------------------------
    // Phase 4 – Domain & messaging.
    // ----------------------------------------------------------------------
    builder.Services.AddSingleton<AlertStore>();

    builder.Services.AddPlatformMassTransit(builder.Configuration, mt =>
    {
        mt.AddConsumer<CriticalAlertTriggeredConsumer>();
        mt.AddConsumer<StopReasonClassifiedConsumer>();
    });

    var app = builder.Build();

    app.UseSerilogRequestLogging();

    if (app.Environment.IsDevelopment())
    {
        app.UseSwagger();
        app.UseSwaggerUI();
    }

    app.UseCors(CorsPolicyName);

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

    // CORS-policyen håndhæves globalt via UseCors ovenfor; SSE-endpointet
    // og audit-endpointet arver politikken uden behov for en eksplicit
    // RequireCors-attribut.
    app.MapAlertStreamEndpoints();
    app.MapAlertClassificationsEndpoints();

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

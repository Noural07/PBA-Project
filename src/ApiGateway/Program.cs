using HealthChecks.UI.Client;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Serilog;
using Pba.Shared.HealthChecks;
using Pba.Shared.Observability;

const string ServiceName = "api-gateway";

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
            Description = "External API gateway routing inbound traffic to internal microservices via YARP."
        });
    });

    // Database-per-Service: ApiGateway er en ren YARP-proxy uden datalag.
    // Postgres-afhængighed ville være en falsk health-signal og er derfor
    // udeladt jf. bounded-context-princippet.
    builder.Services.AddPlatformHealthChecks(builder.Configuration, requirePostgres: false);

    // YARP reverse-proxy konfigureres deklarativt fra appsettings under sektionen
    // "ReverseProxy". Dette holder routing-tabel og clusters separate fra C#-kode
    // og gør konfigurationen overskrivelig pr. miljø via standard ASP.NET Core
    // konfigurationskæde.
    builder.Services.AddReverseProxy()
        .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"));

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

    // YARP reverse-proxy mappes som det sidste endpoint, så lokale routes som
    // /, /health og /swagger ikke utilsigtet videresendes til downstream services.
    app.MapReverseProxy();

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

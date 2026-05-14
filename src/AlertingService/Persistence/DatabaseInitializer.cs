using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AlertingService.Persistence;

/// <summary>
/// <see cref="IHostedService"/> der ved opstart sikrer, at
/// AlertingService' Postgres-skema findes. Ligesom i AnalyzerService
/// anvendes <c>EnsureCreatedAsync</c> i Development-miljøet som let-vægts
/// substitut for en fuld migrations-pipeline. I produktion forventes
/// migrations udrullet eksternt af CI.
/// </summary>
public sealed class DatabaseInitializer : IHostedService
{
    private readonly IServiceProvider _services;
    private readonly IHostEnvironment _environment;
    private readonly ILogger<DatabaseInitializer> _logger;

    public DatabaseInitializer(
        IServiceProvider services,
        IHostEnvironment environment,
        ILogger<DatabaseInitializer> logger)
    {
        _services = services;
        _environment = environment;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_environment.IsDevelopment())
        {
            _logger.LogInformation(
                "DatabaseInitializer er deaktiveret i miljøet {Environment} – migrationer forventes anvendt eksternt.",
                _environment.EnvironmentName);
            return;
        }

        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AlertingDbContext>();

        try
        {
            var created = await db.Database.EnsureCreatedAsync(cancellationToken);
            if (created)
            {
                _logger.LogInformation("AlertingDbContext: skemaet blev oprettet (EnsureCreated).");
            }
            else
            {
                _logger.LogInformation("AlertingDbContext: skemaet eksisterede allerede.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fejl under initialisering af AlertingDbContext.");
            throw;
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

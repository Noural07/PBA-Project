using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AnalyzerService.Persistence;

/// <summary>
/// Hosted service der ved opstart kalder <c>EnsureCreatedAsync</c> (kun i Development).
/// Erstattes med <c>MigrateAsync</c> når en migrations-pipeline introduceres.
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
        var db = scope.ServiceProvider.GetRequiredService<AnalyzerDbContext>();

        try
        {
            var created = await db.Database.EnsureCreatedAsync(cancellationToken);
            if (created)
            {
                _logger.LogInformation("AnalyzerDbContext: skemaet blev oprettet (EnsureCreated).");
            }
            else
            {
                _logger.LogInformation("AnalyzerDbContext: skemaet eksisterede allerede.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fejl under initialisering af AnalyzerDbContext.");
            throw;
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

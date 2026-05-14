using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AnalyzerService.Persistence;

/// <summary>
/// <see cref="IHostedService"/> der ved opstart sikrer, at databaseskemaet
/// findes. I Phase 3 anvendes <c>EnsureCreatedAsync</c> som let-vægts
/// substitut for en fuld migration-historik. Tilgangen er bevidst valgt for
/// at holde Phase 3 fokuseret på domænelogik og event-flow; en egentlig
/// migrations-pipeline introduceres i en senere fase, hvorefter dette
/// hosted service udskiftes med <c>db.Database.MigrateAsync()</c>.
/// </summary>
/// <remarks>
/// Tilgangen er kun aktiveret i <c>Development</c>-miljøet, jf. plan §7.3
/// (DoD: "automatisk migration-run ved opstart (kun i Development)").
/// I produktion forventes skemaet at være migreret eksternt af en CI-pipeline.
/// </remarks>
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

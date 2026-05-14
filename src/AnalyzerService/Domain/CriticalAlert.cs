using System;

namespace AnalyzerService.Domain;

/// <summary>
/// Persistent repræsentation af en kritisk hændelse. Genereres når
/// <c>CriticalRuleEvaluator</c> finder, at en <see cref="Measurement"/>
/// overstiger en eller flere konfigurerede tærskler. Rækken refereres
/// 1:1 fra det publicerede <c>CriticalAlertTriggered</c>-event via
/// <see cref="Id"/>.
/// </summary>
public sealed class CriticalAlert
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid MeasurementId { get; set; }

    public Measurement? Measurement { get; set; }

    public Guid CorrelationId { get; set; }

    public string Severity { get; set; } = "High";

    /// <summary>Den primære regel der udløste alarmen.</summary>
    public string Rule { get; set; } = string.Empty;

    /// <summary>Komma-separeret liste af alle regler, der gav udslag.</summary>
    public string TriggeredRules { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public int TotalDowntimeMinutes { get; set; }

    public string? TopReason { get; set; }

    public string? ObservedCriticalReasons { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

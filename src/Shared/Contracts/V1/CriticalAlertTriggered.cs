using System;
using System.Collections.Generic;

namespace Pba.Shared.Contracts.V1;

/// <summary>
/// Domæne-event der publiceres af <c>AnalyzerService</c>, når en analyseret
/// måling overskrider kritikalitetsregler – enten fordi den samlede nedetid
/// passerer en konfigureret tærskel, eller fordi den hyppigste stopårsag
/// indgår i en kritisk-liste. <c>AlertingService</c> konsumerer eventet og
/// projicerer det videre til SSE-frontend'en.
/// </summary>
public sealed record CriticalAlertTriggered
{
    public Guid EventId { get; init; } = Guid.NewGuid();

    public Guid CorrelationId { get; init; } = Guid.NewGuid();

    public DateTimeOffset OccurredAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Unikt alert-ID, der refererer til <c>CriticalAlerts</c>-rækken i PostgreSQL.</summary>
    public Guid AlertId { get; init; } = Guid.NewGuid();

    /// <summary>Måling der gav anledning til alarmen (eksternt nøglebegreb mod <c>Measurement</c>-tabellen).</summary>
    public Guid MeasurementId { get; init; }

    public int ChannelId { get; init; }

    /// <summary>Severity-niveau ("Low", "Medium", "High"). Phase 3 anvender udelukkende "High".</summary>
    public string Severity { get; init; } = "High";

    /// <summary>Den regel der udløste alarmen (<c>DowntimeExceedsThreshold</c>, <c>CriticalReasonObserved</c>, …).</summary>
    public required string Rule { get; init; }

    /// <summary>Menneskeligt læsbar beskrivelse af alarmens årsag.</summary>
    public required string Description { get; init; }

    public int TotalDowntimeMinutes { get; init; }

    public string? TopReason { get; init; }

    public string? OrderId { get; init; }

    public double? CompletionPct { get; init; }

    /// <summary>De kritiske årsager der blev observeret i tidsvinduet.</summary>
    public IReadOnlyList<string> ObservedCriticalReasons { get; init; } = [];
}

using System;
using System.Collections.Generic;

namespace Pba.Shared.Contracts.V1;

/// <summary>
/// Domæne-event der publiceres af <c>IngestionService</c>, når en batch af
/// Trendlog-feeds er normaliseret og aggregeret. Eventet repræsenterer
/// et logisk tidsvindue (typisk en arbejdsdag eller en mindre delperiode)
/// for én produktionskanal og indeholder de aggregerede produktions- og
/// nedetidstal, som <c>AnalyzerService</c> evaluerer mod kritikalitetsregler.
/// </summary>
/// <remarks>
/// <para>
/// Kontrakten holdes bevidst flad og value-baseret (record + immutable
/// collections). Dette letter både serialisering via MassTransit's RabbitMQ-
/// transport og enhedstest af forretningsregler i <c>AnalyzerService</c>.
/// </para>
/// <para>
/// Alle nedstrøms-events arver <see cref="CorrelationId"/> uændret, så et
/// fuldt hændelsesforløb kan rekonstrueres i Grafana via en LogQL-query
/// af typen <c>{job=~"pba-.*"} |= "&lt;correlationId&gt;"</c>.
/// </para>
/// </remarks>
public sealed record MeasurementReceived
{
    /// <summary>Unikt event-ID. Genereres af afsenderen.</summary>
    public Guid EventId { get; init; } = Guid.NewGuid();

    /// <summary>
    /// Korrelations-ID der bæres uændret igennem hele event-pipelinen og
    /// muliggør end-to-end-sporing af et logisk forløb på tværs af services.
    /// </summary>
    public Guid CorrelationId { get; init; } = Guid.NewGuid();

    /// <summary>Tidspunkt for udstedelse af eventet (UTC).</summary>
    public DateTimeOffset OccurredAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Trendlog-kanal-ID (eksempelvis 20).</summary>
    public int ChannelId { get; init; }

    /// <summary>Start-tidspunkt for det aggregerede tidsvindue (UTC).</summary>
    public DateTimeOffset WindowStart { get; init; }

    /// <summary>Slut-tidspunkt for det aggregerede tidsvindue (UTC).</summary>
    public DateTimeOffset WindowEnd { get; init; }

    /// <summary>Akkumuleret produceret kvantum i tidsvinduet (sum af <c>XYZ01_cnt</c>-diffs).</summary>
    public int ProducedUnits { get; init; }

    /// <summary>Akkumuleret køretid i sekunder (sum af <c>XYZ01_runtime</c>-diffs).</summary>
    public int RuntimeSeconds { get; init; }

    /// <summary>Samlet nedetid i tidsvinduet (sum af alle stoptider, minutter).</summary>
    public int TotalDowntimeMinutes { get; init; }

    /// <summary>Hyppigste/mest dominerende stopårsag (valgt efter samlet varighed).</summary>
    public string? TopReason { get; init; }

    /// <summary>Aktuel ordre-ID, hvis kendt (Trendlog-feed <c>XYZ01_porder</c>).</summary>
    public string? OrderId { get; init; }

    /// <summary>Måltal/kvantum for ordren, hvis kendt.</summary>
    public int? OrderTarget { get; init; }

    /// <summary>Færdiggørelsesgrad i procent (<c>ProducedUnits / OrderTarget * 100</c>), hvis ordren er kendt.</summary>
    public double? CompletionPct { get; init; }

    /// <summary>
    /// Aggregerede stopårsager grupperet efter <c>reason</c> med summeret varighed.
    /// </summary>
    public IReadOnlyList<StopReasonAggregate> StopReasons { get; init; } = [];
}

/// <summary>
/// Aggregeret repræsentation af en stopårsag inden for et tidsvindue.
/// </summary>
public sealed record StopReasonAggregate
{
    /// <summary>Den fri-tekstbaserede stopårsag som registreret af operatøren.</summary>
    public required string Reason { get; init; }

    /// <summary>Summeret varighed i minutter for denne årsag.</summary>
    public required int DurationMinutes { get; init; }

    /// <summary>Antal forekomster af denne årsag i tidsvinduet.</summary>
    public required int Occurrences { get; init; }
}

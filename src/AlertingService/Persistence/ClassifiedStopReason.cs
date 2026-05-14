using System;

namespace AlertingService.Persistence;

/// <summary>
/// Audit-projektion af et indkommet <c>StopReasonClassified</c>-event.
/// Persisteres i Postgres-tabellen <c>classified_stop_reasons</c> for at
/// give et fuldt sporbart, langtidsholdbart historisk register over alle
/// AI-klassifikationer der er strømmet gennem pipelinen — supplerende den
/// in-memory ring-buffer der serverer SSE-frontend'en.
/// </summary>
/// <remarks>
/// <para>
/// Tabellen er bevidst flad og denormaliseret: hver række er en uafhængig
/// historisk observation. Dette gør den brugbar både til ad hoc-queries
/// fra rapport-værktøjer (Grafana Postgres-datasource, pg_dump-eksport)
/// og til regression af AI-output, hvor man kan sammenligne nye modeller
/// mod historiske klassifikationer for samme fri-tekst.
/// </para>
/// <para>
/// <b>Idempotens.</b> <see cref="StopEventId"/> er unik på rækken (jf.
/// indekset i <see cref="AlertingDbContext"/>), således at en eventuel
/// genleverance af det samme event fra MassTransit's retry-pipeline ikke
/// skaber dubletter.
/// </para>
/// </remarks>
public sealed class ClassifiedStopReason
{
    /// <summary>
    /// Auto-genereret rækkenøgle. Anvendes ikke uden for tabellen og
    /// erstatter ikke <see cref="EventId"/> som event-identifikation.
    /// </summary>
    public long Id { get; set; }

    /// <summary>EventId fra det publicerede <c>StopReasonClassified</c>-event.</summary>
    public Guid EventId { get; set; }

    /// <summary>CorrelationId der binder klassifikationen til dens kritiske alarm.</summary>
    public Guid CorrelationId { get; set; }

    /// <summary>StopEventId arvet fra <c>OperatorCommentRegistered</c>.</summary>
    public Guid StopEventId { get; set; }

    public DateTimeOffset OccurredAt { get; set; }

    public int ChannelId { get; set; }

    public string OriginalReason { get; set; } = string.Empty;

    public string Category { get; set; } = string.Empty;

    public string Subcategory { get; set; } = string.Empty;

    public string StandardizedReason { get; set; } = string.Empty;

    public string Severity { get; set; } = string.Empty;

    public string RecommendedAction { get; set; } = string.Empty;

    public double Confidence { get; set; }

    public long LatencyMs { get; set; }

    public bool IsFallback { get; set; }
}

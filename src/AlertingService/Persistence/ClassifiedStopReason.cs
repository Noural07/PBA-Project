using System;

namespace AlertingService.Persistence;

/// <summary>
/// Audit-entitet der persisterer ét <c>StopReasonClassified</c>-event i <c>classified_stop_reasons</c>.
/// </summary>

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

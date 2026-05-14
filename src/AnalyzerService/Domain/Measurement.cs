using System;
using System.Collections.Generic;

namespace AnalyzerService.Domain;

/// <summary>
/// Persistent repræsentation af et aggregeret tidsvindue for én Trendlog-kanal.
/// Modellen opbevarer både rå-aggregater (produktion, runtime, nedetid) og
/// resultatet af kritikalitetsevalueringen (<see cref="IsCritical"/>), således
/// at <c>AnalyzerService</c> både kan svare på inspektions-forespørgsler
/// (<c>GET /measurements?orderId=...</c>) og udgøre fakta-grundlaget for
/// <c>AlertingService</c>'s konsolidering i Phase 4.
/// </summary>
public sealed class Measurement
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid CorrelationId { get; set; }

    public int ChannelId { get; set; }

    public DateTimeOffset WindowStart { get; set; }

    public DateTimeOffset WindowEnd { get; set; }

    public int ProducedUnits { get; set; }

    public int RuntimeSeconds { get; set; }

    public int TotalDowntimeMinutes { get; set; }

    public string? TopReason { get; set; }

    public string? OrderId { get; set; }

    public int? OrderTarget { get; set; }

    public double? CompletionPct { get; set; }

    public bool IsCritical { get; set; }

    public DateTimeOffset ReceivedAt { get; set; } = DateTimeOffset.UtcNow;

    public List<StopEvent> StopEvents { get; set; } = [];

    public CriticalAlert? CriticalAlert { get; set; }
}

using System;

namespace AnalyzerService.Domain;

/// <summary>
/// Persistent repræsentation af én aggregeret stopårsag inden for et
/// måling-tidsvindue. Tabellens granularitet matcher
/// <c>StopReasonAggregate</c> i kontrakten <c>MeasurementReceived</c>:
/// én række pr. unik <c>reason</c> pr. <see cref="Measurement"/>.
/// </summary>
public sealed class StopEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid MeasurementId { get; set; }

    public Measurement? Measurement { get; set; }

    public string Reason { get; set; } = string.Empty;

    public int DurationMinutes { get; set; }

    public int Occurrences { get; set; }

    /// <summary>
    /// Markerer om årsagen er klassificeret som kritisk efter den indlejrede
    /// kritisk-liste i <c>AnalyzerService</c>'s regelmotor.
    /// </summary>
    public bool IsCriticalReason { get; set; }
}

using System;

namespace Pba.Shared.Contracts.V1;

/// <summary>
/// Domæne-event der repræsenterer en enkelt operatørregistreret stopårsag
/// (fri-tekst). Konsumenten i Phase 4 er <c>AiService</c>, som klassificerer
/// teksten via Gemini-API'et og publicerer <c>StopReasonClassified</c>.
/// </summary>
/// <remarks>
/// Eventet udstedes af <c>IngestionService</c> for hvert <c>XYZ01_stoptime</c>-
/// feed-element. <c>CorrelationId</c> arves fra det tilhørende
/// <see cref="MeasurementReceived"/>-event når en hel batch normaliseres,
/// således at AI-klassifikationen kan korreleres med de aggregerede måleværdier
/// i <c>AlertingService</c>.
/// </remarks>
public sealed record OperatorCommentRegistered
{
    public Guid EventId { get; init; } = Guid.NewGuid();

    public Guid CorrelationId { get; init; } = Guid.NewGuid();

    public DateTimeOffset OccurredAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Stop-event-ID. Anvendes som naturlig nøgle i AI-klassifikationen.</summary>
    public Guid StopEventId { get; init; } = Guid.NewGuid();

    /// <summary>Trendlog-kanal-ID.</summary>
    public int ChannelId { get; init; }

    /// <summary>Tidspunkt for stop-eventet (UTC).</summary>
    public DateTimeOffset Timestamp { get; init; }

    /// <summary>Operatørens fri-tekst-årsag som modtaget fra Trendlog.</summary>
    public required string Reason { get; init; }

    /// <summary>
    /// Operatør-tildelt kategori (fx <c>Fault</c>, <c>Reload</c>) — hvis Trendlog
    /// leverede et struktureret <c>comment</c>-felt på dict-form. Felt er nullable
    /// fordi simulerede payloads og ældre Trendlog-feeds ikke nødvendigvis bærer
    /// kategorien adskilt fra fri-teksten.
    /// </summary>
    public string? Category { get; init; }

    /// <summary>Varighed i minutter for det enkelte stop.</summary>
    public required int DurationMinutes { get; init; }

    /// <summary>Tilknyttet ordre-ID, hvis kendt.</summary>
    public string? OrderId { get; init; }
}

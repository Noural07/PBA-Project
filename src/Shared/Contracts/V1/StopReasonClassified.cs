using System;

namespace Pba.Shared.Contracts.V1;

/// <summary>
/// Domæne-event der publiceres af <c>AiService</c>, når en operatørregistreret
/// fri-tekst stopårsag er blevet kategoriseret af Gemini-modellen. Eventet
/// konsumeres af <c>AlertingService</c>, der korrelerer klassifikationen med
/// den tilhørende kritiske alarm via <see cref="CorrelationId"/> og projicerer
/// resultatet til frontend'en via SSE-streamen samt persisterer det til
/// Postgres som auditerbar log.
/// </summary>
/// <remarks>
/// <para>
/// Kontrakten anvender et <em>hybridt</em> klassifikationsskema, jf. fase C-
/// designvalget: Trendlogs egen grov-kategori (<c>Fault</c>, <c>Maintenance</c>,
/// <c>Break</c>, <c>Other</c>) bevares i <see cref="Category"/>, mens
/// <see cref="Subcategory"/> indeholder en mere specifik, dansksproget
/// underkategori udledt af Gemini ud fra fri-tekst og varighed. Frontend'en
/// kan dermed både filtrere på en stabil grov-kategori og vise en menneske-
/// læsbar nuance.
/// </para>
/// <para>
/// <see cref="StopEventId"/> arves fra <c>OperatorCommentRegistered</c>, så
/// det er muligt at sammenkoble klassifikationen med den enkelte operatør-
/// kommentar i Grafana via en LogQL-query af typen
/// <c>{job=~"pba-.*"} |= "&lt;stopEventId&gt;"</c> eller via Postgres-
/// auditeringstabellens unikke nøgle.
/// </para>
/// <para>
/// <see cref="LatencyMs"/> rapporterer det rå netværks-/inferens-budget for
/// kaldet til Gemini og er udelukkende beregnet til performance-overvågning;
/// feltet inkluderer ikke tidsforbrug i Polly-pipeline (køtid for retries
/// rapporteres separat i <c>Gemini.Resilience</c>-loggen).
/// </para>
/// </remarks>
public sealed record StopReasonClassified
{
    public Guid EventId { get; init; } = Guid.NewGuid();

    public Guid CorrelationId { get; init; } = Guid.NewGuid();

    public DateTimeOffset OccurredAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Refererer til det oprindelige stop-event (jf. <c>OperatorCommentRegistered.StopEventId</c>).</summary>
    public Guid StopEventId { get; init; }

    public int ChannelId { get; init; }

    /// <summary>Operatørens originale fri-tekst – bevares for sporbarhed.</summary>
    public required string OriginalReason { get; init; }

    /// <summary>
    /// Trendlog-grov-kategori udtrukket af <c>PythonCommentParser</c> i Fase B.
    /// Et af et lille, fast vokabular: <c>Fault</c>, <c>Maintenance</c>,
    /// <c>Break</c>, <c>Other</c>, eller <c>Unclassified</c> ved fallback.
    /// </summary>
    public required string Category { get; init; }

    /// <summary>
    /// Dansk, specifik underkategori udledt af Gemini, fx
    /// <c>"Mekanisk havari"</c>, <c>"Planlagt rengøring"</c>,
    /// <c>"Frokostpause"</c>. Maksimal længde 64 tegn.
    /// </summary>
    public required string Subcategory { get; init; }

    /// <summary>
    /// Kort, ensrettet beskrivelse på dansk (maks. 80 tegn) afledt af Gemini.
    /// Anvendes som primær visningsstreng i frontend'en.
    /// </summary>
    public required string StandardizedReason { get; init; }

    /// <summary>
    /// Modellens estimerede alvorlighedsgrad: <c>Low</c>, <c>Medium</c>,
    /// <c>High</c> eller <c>Critical</c>. Bruges i frontend'en til at
    /// supplere AnalyzerServices regel-baserede severity og kan i
    /// fremtidige faser fodre en ESI-konsolidator.
    /// </summary>
    public required string Severity { get; init; }

    /// <summary>
    /// Kort, dansksproget anbefaling til operatøren (maks. 120 tegn). Eksempler:
    /// <c>"Tilkald maskinmester"</c>, <c>"Genstart linje efter rengøring"</c>.
    /// Modellen kan returnere en tom streng hvis ingen specifik handling
    /// kan udledes; dette behandles ikke som en fejl.
    /// </summary>
    public required string RecommendedAction { get; init; }

    /// <summary>Modellens tillid til klassifikationen i intervallet [0;1].</summary>
    public double Confidence { get; init; }

    /// <summary>
    /// Mål for det enkelte Gemini-kalds end-to-end-tid i millisekunder. Bruges
    /// til performance-overvågning og kapacitetsplanlægning. Værdien er
    /// <c>0</c> ved fallback, hvor der ikke blev foretaget et reelt kald.
    /// </summary>
    public long LatencyMs { get; init; }

    /// <summary>
    /// Indikerer om klassifikationen blev fremstillet via fallback (eksempelvis fordi
    /// API-kaldet fejlede eller modellen returnerede ugyldig JSON). Bevares så
    /// frontend'en kan visualisere usikkerheden separat og så Postgres-
    /// auditeringen dokumenterer at en eventuel kategori er udledt deterministisk
    /// frem for fra modellen.
    /// </summary>
    public bool IsFallback { get; init; }
}

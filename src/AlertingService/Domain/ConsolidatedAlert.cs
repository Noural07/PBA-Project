using System;
using System.Text.Json.Serialization;

namespace AlertingService.Domain;

/// <summary>
/// Det konsoliderede alarm-projekt der eksponeres via SSE-streamen til
/// frontend'en. Klassen er en flad, serialiserbar projektion, hvor en
/// kritisk alarm fra <see cref="Pba.Shared.Contracts.V1.CriticalAlertTriggered"/>
/// er sammenkoblet med den senest tilgængelige AI-klassifikation
/// (<see cref="Pba.Shared.Contracts.V1.StopReasonClassified"/>) for samme
/// <c>CorrelationId</c>.
/// </summary>
/// <remarks>
/// Klassen er bevidst designet som et <em>read-model</em>: den eneste hensigt
/// er at servicere SSE-frontend'en og at dække Loki-loggens behov for
/// strukturerede felter. Domæne-aggregaterne ejes af <c>AnalyzerService</c>
/// og <c>AiService</c>; <c>AlertingService</c> opererer kun på events og
/// projektioner. Auditerbar persistens af AI-klassifikationerne håndteres
/// separat i <c>AlertingService.Persistence.ClassifiedStopReason</c>.
/// </remarks>
public sealed record ConsolidatedAlert
{
    [JsonPropertyName("alertId")]
    public required Guid AlertId { get; init; }

    [JsonPropertyName("correlationId")]
    public required Guid CorrelationId { get; init; }

    [JsonPropertyName("timestamp")]
    public required DateTimeOffset Timestamp { get; init; }

    [JsonPropertyName("channelId")]
    public required int ChannelId { get; init; }

    [JsonPropertyName("severity")]
    public required string Severity { get; init; }

    [JsonPropertyName("rule")]
    public required string Rule { get; init; }

    [JsonPropertyName("description")]
    public required string Description { get; init; }

    [JsonPropertyName("totalDowntimeMinutes")]
    public required int TotalDowntimeMinutes { get; init; }

    [JsonPropertyName("topReason")]
    public string? TopReason { get; init; }

    [JsonPropertyName("orderId")]
    public string? OrderId { get; init; }

    // ---- AI-klassifikationsfelter (Fase C) -------------------------------

    [JsonPropertyName("aiCategory")]
    public string? AiCategory { get; init; }

    [JsonPropertyName("aiSubcategory")]
    public string? AiSubcategory { get; init; }

    [JsonPropertyName("aiStandardizedReason")]
    public string? AiStandardizedReason { get; init; }

    [JsonPropertyName("aiSeverity")]
    public string? AiSeverity { get; init; }

    [JsonPropertyName("aiRecommendedAction")]
    public string? AiRecommendedAction { get; init; }

    [JsonPropertyName("aiConfidence")]
    public double? AiConfidence { get; init; }

    [JsonPropertyName("aiLatencyMs")]
    public long? AiLatencyMs { get; init; }

    [JsonPropertyName("aiIsFallback")]
    public bool? AiIsFallback { get; init; }

    /// <summary>
    /// Den <c>OriginalReason</c> der har leveret de aktuelt-viste AI-felter.
    /// Bruges af <c>AlertStore</c> til at afgøre, om en ny
    /// <c>StopReasonClassified</c> for samme korrelations-ID skal overskrive
    /// felterne (kun hvis <c>OriginalReason</c> matcher <c>TopReason</c>),
    /// eller om de øvrige klassifikationer alene skal logges til
    /// <c>classified_stop_reasons</c>. Eksponeres i SSE-payload'en så
    /// frontend'en evt. kan badge'e rækken som "matchet" vs. "ventende".
    /// </summary>
    [JsonPropertyName("aiOriginalReason")]
    public string? AiOriginalReason { get; init; }
}

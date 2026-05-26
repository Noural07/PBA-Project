using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace IngestionService.Trendlog;

/// <summary>
/// Internt batch-DTO der samler alle Trendlog-feeds i én konvolut til normalisering.
/// </summary>

public sealed class TrendlogBatchPayload
{
    [JsonPropertyName("channelId")]
    public int ChannelId { get; set; } = 20;

    [JsonPropertyName("windowStart")]
    public DateTimeOffset WindowStart { get; set; } = DateTimeOffset.UtcNow.AddHours(-1);

    [JsonPropertyName("windowEnd")]
    public DateTimeOffset WindowEnd { get; set; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("feeds")]
    public TrendlogFeeds Feeds { get; set; } = new();
}

public sealed class TrendlogFeeds
{
    [JsonPropertyName("XYZ01_stoptime")]
    public List<TrendlogStopEntry> Stoptime { get; set; } = [];

    [JsonPropertyName("XYZ01_cnt")]
    public List<TrendlogDiffEntry> Count { get; set; } = [];

    [JsonPropertyName("XYZ01_runtime")]
    public List<TrendlogDiffEntry> Runtime { get; set; } = [];

    [JsonPropertyName("XYZ01_porder")]
    public List<TrendlogOrderEntry> Order { get; set; } = [];
}

public sealed class TrendlogStopEntry
{
    [JsonPropertyName("timestamp")]
    public DateTimeOffset Timestamp { get; set; }

    [JsonPropertyName("reason")]
    public string Reason { get; set; } = string.Empty;

    [JsonPropertyName("durationMinutes")]
    public int DurationMinutes { get; set; }

    /// <summary>
    /// Operatør-kategori udtrukket fra Trendlogs <c>comment</c>-felt
    /// (fx <c>Fault</c>, <c>Reload</c>). Holdes adskilt fra <see cref="Reason"/>
    /// for at gøre det muligt for AI-laget i Fase C at klassificere på basis
    /// af både kategori og fri-tekst.
    /// </summary>
    [JsonPropertyName("category")]
    public string? Category { get; set; }

    /// <summary>
    /// Trendlogs <c>pointid</c> (fx <c>"2687278"</c>). Bevares som naturlig
    /// nøgle, så <see cref="Pba.Shared.Contracts.V1.OperatorCommentRegistered.StopEventId"/>
    /// kan udledes deterministisk og dermed give idempotente events ved gen-kald.
    /// </summary>
    [JsonPropertyName("sourcePointId")]
    public string? SourcePointId { get; set; }
}

public sealed class TrendlogDiffEntry
{
    [JsonPropertyName("timestamp")]
    public DateTimeOffset Timestamp { get; set; }

    [JsonPropertyName("diff")]
    public int Diff { get; set; }
}

public sealed class TrendlogOrderEntry
{
    [JsonPropertyName("timestamp")]
    public DateTimeOffset Timestamp { get; set; }

    [JsonPropertyName("orderId")]
    public string OrderId { get; set; } = string.Empty;

    [JsonPropertyName("kvantum")]
    public int Kvantum { get; set; }
}

/// <summary>
/// Quick-path payload til <c>POST /ingestion/stop-event</c>: Et enkelt
/// stop-event uden tilhørende batch. Anvendes typisk under demo og tests
/// hvor AI-klassifikation skal afprøves uden at konstruere et fuldt
/// Trendlog-respons.
/// </summary>
public sealed class StopEventPayload
{
    [JsonPropertyName("channelId")]
    public int ChannelId { get; set; } = 20;

    [JsonPropertyName("timestamp")]
    public DateTimeOffset? Timestamp { get; set; }

    [JsonPropertyName("reason")]
    public string Reason { get; set; } = string.Empty;

    [JsonPropertyName("durationMinutes")]
    public int DurationMinutes { get; set; }

    [JsonPropertyName("orderId")]
    public string? OrderId { get; set; }
}

/// <summary>
/// Specifikation af én feed-anmodning der indgår i POST-bodyen til
/// Trendlog's <c>/V1/channels/{id}/feeds</c>-endpoint. Hver feed kræver
/// både et <c>feedid</c> (tekstuelt navn som <c>XYZ01_stoptime</c>) og en
/// <c>viewid</c> (numerisk visningsidentifier som varierer per feed).
/// Et array af disse objekter sendes som JSON-body.
/// </summary>
public sealed class TrendlogFeedRequest
{
    [JsonPropertyName("feedid")]
    public string FeedId { get; set; } = string.Empty;

    /// <summary>
    /// Aggregeringsmetode. Verificeret værdi: <c>"diff"</c>.
    /// </summary>
    [JsonPropertyName("method")]
    public string Method { get; set; } = "diff";

    /// <summary>
    /// Hvis <c>true</c> bortfiltreres negative værdier af Trendlog selv.
    /// </summary>
    [JsonPropertyName("noneg")]
    public bool Noneg { get; set; }

    /// <summary>
    /// Visnings-id (varierer per feed). Kendt for kanal 20:
    /// <c>XYZ01_stoptime → 9668</c>. Øvrige viewids skal kortlægges.
    /// </summary>
    [JsonPropertyName("viewid")]
    public int ViewId { get; set; }
}

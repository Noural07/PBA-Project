using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace IngestionService.Trendlog;

/// <summary>
/// Konverterer det rå <see cref="JsonElement"/>-respons fra Trendlog til
/// projektets interne <see cref="TrendlogBatchPayload"/>-form. Mapperen er
/// designet til at være tolerant over for de mange små afvigelser, der
/// observeres i live-feeds:
/// <list type="bullet">
///   <item><description>Manglende feeds (ikke alle kanaler har alle fire test-feeds).</description></item>
///   <item><description>Tomme <c>points</c>-arrays (en feed kan have nul målinger i et tidsvindue).</description></item>
///   <item><description>Numeriske værdier leveret som strings (<c>"0"</c>, <c>"398"</c>, <c>"-260"</c>).</description></item>
///   <item><description>Tidsstempler i formatet <c>"yyyy-MM-dd HH:mm:ss.fff"</c> uden zone-suffix.</description></item>
///   <item><description>Operatør-kommentarer på Python-dict-stringificeret form (kun stoptime-feedet).</description></item>
/// </list>
/// Mappen logger advarsler ved hver dataanomali, men kaster ikke exception
/// — dette holder pipelinen kørende selv ved partielle datafejl, hvilket er
/// nødvendigt under demonstrationen, jf. projektregel om production-readiness.
/// </summary>
public sealed class TrendlogResponseMapper : ITrendlogResponseMapper
{
    /// <summary>
    /// Trendlog leverer tidsstempler uden zoneinformation. Konventionen i den
    /// observerede live-data er, at tidsstempler er i UTC (Trendlogs egen
    /// dokumentation bekræfter dette). Antagelsen dokumenteres i fase B-rapporten.
    /// </summary>
    private static readonly string[] TimestampFormats =
    [
        "yyyy-MM-dd HH:mm:ss.fff",
        "yyyy-MM-dd HH:mm:ss",
        "yyyy-MM-ddTHH:mm:ss.fffK",
        "yyyy-MM-ddTHH:mm:ssK"
    ];

    private const string FeedNameStoptime = "XYZ01_stoptime";
    private const string FeedNameCount = "XYZ01_cnt";
    private const string FeedNameRuntime = "XYZ01_runtime";
    private const string FeedNameOrder = "XYZ01_porder";

    /// <summary>
    /// Mulige feltnavne der bærer feedets <em>tekstuelle</em> identifier i
    /// Trendlogs respons (fx <c>"XYZ01_stoptime"</c>). Prioriteret rækkefølge:
    /// <list type="number">
    ///   <item><description><c>name</c> — Trendlogs faktisk anvendte felt for tekstuelt feed-navn.</description></item>
    ///   <item><description><c>feedid</c> — echo af request-feltet, observeret i ældre versioner.</description></item>
    ///   <item><description><c>feed_name</c> — alternativ navnevariant.</description></item>
    /// </list>
    /// Bemærk: feltet <c>feed_id</c> bevidst <em>udeladt</em>, da det indeholder
    /// en numerisk identifier (fx <c>"30119"</c>) — ikke det tekstuelle navn —
    /// og ville få mapperens switch til at fejle uigenkendeligt.
    /// </summary>
    private static readonly string[] FeedNameKeys =
    [
        "name",
        "feedid",
        "feed_name"
    ];

    /// <summary>
    /// Mulige feltnavne for det indre points-array. Trendlog leverer typisk
    /// <c>points</c>; <c>data</c> ses i ældre versioner.
    /// </summary>
    private static readonly string[] PointsKeys = ["points", "data"];

    private readonly ILogger<TrendlogResponseMapper> _logger;

    public TrendlogResponseMapper(ILogger<TrendlogResponseMapper> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public TrendlogBatchPayload Map(
        JsonElement raw,
        int channelId,
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd)
    {
        if (raw.ValueKind == JsonValueKind.Undefined || raw.ValueKind == JsonValueKind.Null)
        {
            _logger.LogWarning("Trendlog-respons var tom; returnerer tom batch.");
            return EmptyBatch(channelId, windowStart, windowEnd);
        }

        var payload = new TrendlogBatchPayload
        {
            ChannelId = ResolveChannelId(raw, channelId),
            WindowStart = windowStart,
            WindowEnd = windowEnd,
            Feeds = new TrendlogFeeds()
        };

        if (!TryResolveFeedsArray(raw, out var feedsElement))
        {
            _logger.LogWarning(
                "Trendlog-respons mangler feeds-array på både {ChannelPath} og top-niveau. " +
                "Tilgængelige top-keys: {TopKeys}; pipelinen får tom payload.",
                "channel.feeds",
                DumpKeys(raw));
            return payload;
        }

        foreach (var feedElement in feedsElement.EnumerateArray())
        {
            if (feedElement.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var feedName = ResolveFeedName(feedElement);
            if (string.IsNullOrWhiteSpace(feedName))
            {
                _logger.LogWarning(
                    "Feed-objekt uden genkendelig identifier ignoreret. Tilgængelige keys: {Keys}",
                    DumpKeys(feedElement));
                continue;
            }

            if (!TryGetPointsArray(feedElement, out var pointsElement))
            {
                _logger.LogInformation(
                    "Feed {FeedName} har intet genkendeligt points-array; springes over.",
                    feedName);
                continue;
            }

            switch (feedName)
            {
                case FeedNameStoptime:
                    MapStoptimeFeed(pointsElement, payload.Feeds.Stoptime);
                    break;
                case FeedNameCount:
                    MapDiffFeed(pointsElement, payload.Feeds.Count, feedName);
                    break;
                case FeedNameRuntime:
                    MapDiffFeed(pointsElement, payload.Feeds.Runtime, feedName);
                    break;
                case FeedNameOrder:
                    MapOrderFeed(pointsElement, payload.Feeds.Order);
                    break;
                default:
                    // XYZ01_run, samt evt. fremtidige feeds, har ingen target-slot.
                    // Dette er ikke en fejl — mapperen er bevidst additiv.
                    _logger.LogDebug(
                        "Ukendt eller uudnyttet feed {FeedName} springes over i mapping.",
                        feedName);
                    break;
            }
        }

        _logger.LogInformation(
            "Trendlog-respons mappet. Kanal={ChannelId} Stoptime={StopCount} Cnt={CntCount} Runtime={RuntimeCount} Order={OrderCount}",
            payload.ChannelId,
            payload.Feeds.Stoptime.Count,
            payload.Feeds.Count.Count,
            payload.Feeds.Runtime.Count,
            payload.Feeds.Order.Count);

        return payload;
    }

    private static TrendlogBatchPayload EmptyBatch(
        int channelId, DateTimeOffset windowStart, DateTimeOffset windowEnd)
        => new()
        {
            ChannelId = channelId,
            WindowStart = windowStart,
            WindowEnd = windowEnd,
            Feeds = new TrendlogFeeds()
        };

    /// <summary>
    /// Trendlogs faktiske respons-struktur indlejrer feed-arrayet inde i
    /// <c>channel</c>-objektet (<c>raw.channel.feeds[]</c>). Den oprindelige
    /// antagelse i Fase A om et top-level <c>feeds</c>-array var forkert og er
    /// her korrigeret. Fallback til top-level bevares, så mapperen er
    /// fremadkompatibel hvis Trendlog senere flader strukturen ud.
    /// </summary>
    private static bool TryResolveFeedsArray(JsonElement raw, out JsonElement feeds)
    {
        feeds = default;

        if (raw.TryGetProperty("channel", out var channelElement)
            && channelElement.ValueKind == JsonValueKind.Object
            && channelElement.TryGetProperty("feeds", out var nestedFeeds)
            && nestedFeeds.ValueKind == JsonValueKind.Array)
        {
            feeds = nestedFeeds;
            return true;
        }

        if (raw.TryGetProperty("feeds", out var topLevelFeeds)
            && topLevelFeeds.ValueKind == JsonValueKind.Array)
        {
            feeds = topLevelFeeds;
            return true;
        }

        return false;
    }

    private static string ResolveFeedName(JsonElement feedElement)
    {
        foreach (var key in FeedNameKeys)
        {
            if (feedElement.TryGetProperty(key, out var prop)
                && prop.ValueKind == JsonValueKind.String)
            {
                var value = prop.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }
        }

        return string.Empty;
    }

    private static bool TryGetPointsArray(JsonElement feedElement, out JsonElement points)
    {
        foreach (var key in PointsKeys)
        {
            if (feedElement.TryGetProperty(key, out var prop)
                && prop.ValueKind == JsonValueKind.Array)
            {
                points = prop;
                return true;
            }
        }

        points = default;
        return false;
    }

    private static string DumpKeys(JsonElement obj)
    {
        if (obj.ValueKind != JsonValueKind.Object)
        {
            return $"<{obj.ValueKind}>";
        }

        var keys = new List<string>();
        foreach (var prop in obj.EnumerateObject())
        {
            keys.Add(prop.Name);
        }

        return keys.Count == 0
            ? "<empty>"
            : string.Join(", ", keys);
    }

    private static int ResolveChannelId(JsonElement raw, int fallback)
    {
        if (raw.TryGetProperty("channel", out var channelElement)
            && channelElement.ValueKind == JsonValueKind.Object
            && channelElement.TryGetProperty("channel_id", out var idElement))
        {
            if (idElement.ValueKind == JsonValueKind.Number
                && idElement.TryGetInt32(out var numericId))
            {
                return numericId;
            }

            if (idElement.ValueKind == JsonValueKind.String
                && int.TryParse(idElement.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedId))
            {
                return parsedId;
            }
        }

        return fallback;
    }

    private static void MapStoptimeFeed(JsonElement points, List<TrendlogStopEntry> target)
    {
        foreach (var point in points.EnumerateArray())
        {
            if (point.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (!TryReadTimestamp(point, out var timestamp))
            {
                continue;
            }

            // Trendlog leverer XYZ01_stoptime som en kumulativ stop-tæller, der
            // tikker pr. sekund maskinen er stoppet. Med method=diff returnerer
            // hver point varigheden af det enkelte stop i sekunder.
            if (!TryReadIntFromString(point, "value", out var stopSeconds))
            {
                continue;
            }

            // Et "stop" med 0 sekunder indeholder ingen nedetid; springes over,
            // men en evt. tilknyttet kommentar tabes intentionelt — det er
            // ikke et faktisk stop.
            if (stopSeconds <= 0)
            {
                continue;
            }

            var commentRaw = point.TryGetProperty("comment", out var commentProp)
                && commentProp.ValueKind == JsonValueKind.String
                    ? commentProp.GetString()
                    : null;

            var parsedComment = PythonCommentParser.Parse(commentRaw);
            var reasonText = PythonCommentParser.ComposeReason(parsedComment);

            // Hvis Trendlog ikke leverede en tolkbar comment, opretholdes en
            // tom Reason — normaliseringspipelinen filtrerer disse fra
            // operatør-comment-eventet.
            var pointId = point.TryGetProperty("pointid", out var pidProp)
                && pidProp.ValueKind == JsonValueKind.String
                    ? pidProp.GetString()
                    : null;

            // Konvertering sekunder -> minutter sker her, så normaliseringslaget
            // forbliver enheds-uafhængigt. For meget korte stop (<60s) afrundes
            // op til 1 minut for at sikre, at ingen nedetid tabes i aggregatet.
            var durationMinutes = (int)Math.Max(1, Math.Ceiling(stopSeconds / 60.0));

            target.Add(new TrendlogStopEntry
            {
                Timestamp = timestamp,
                Reason = reasonText,
                Category = parsedComment.Category,
                DurationMinutes = durationMinutes,
                SourcePointId = pointId
            });
        }
    }

    private void MapDiffFeed(JsonElement points, List<TrendlogDiffEntry> target, string feedName)
    {
        foreach (var point in points.EnumerateArray())
        {
            if (point.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (!TryReadTimestamp(point, out var timestamp))
            {
                continue;
            }

            if (!TryReadIntFromString(point, "value", out var diff))
            {
                continue;
            }

            target.Add(new TrendlogDiffEntry
            {
                Timestamp = timestamp,
                Diff = diff
            });

            if (diff < 0)
            {
                _logger.LogWarning(
                    "Negativ diff observeret i feed {FeedName} ({Diff}); videreført til normaliseringen for anomali-detektion.",
                    feedName, diff);
            }
        }
    }

    private static void MapOrderFeed(JsonElement points, List<TrendlogOrderEntry> target)
    {
        foreach (var point in points.EnumerateArray())
        {
            if (point.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (!TryReadTimestamp(point, out var timestamp))
            {
                continue;
            }

            // Ordre-feedet er svagt dokumenteret i Trendlog. Live-data antages at
            // bære ordre-id og kvantum i kommentar-feltet på Python-dict-form,
            // alternativt direkte i value/orderId-felter. Mapperen forsøger
            // begge varianter.
            string? orderId = null;
            int kvantum = 0;

            if (point.TryGetProperty("orderId", out var orderIdProp)
                && orderIdProp.ValueKind == JsonValueKind.String)
            {
                orderId = orderIdProp.GetString();
            }
            else if (point.TryGetProperty("comment", out var orderCommentProp)
                && orderCommentProp.ValueKind == JsonValueKind.String)
            {
                var parsed = PythonCommentParser.Parse(orderCommentProp.GetString());
                orderId = parsed.Reason ?? parsed.Category;
            }

            if (TryReadIntFromString(point, "kvantum", out var parsedKvantum))
            {
                kvantum = parsedKvantum;
            }
            else if (TryReadIntFromString(point, "value", out var fallbackValue))
            {
                kvantum = fallbackValue;
            }

            target.Add(new TrendlogOrderEntry
            {
                Timestamp = timestamp,
                OrderId = orderId ?? string.Empty,
                Kvantum = kvantum
            });
        }
    }

    private static bool TryReadTimestamp(JsonElement point, out DateTimeOffset timestamp)
    {
        timestamp = default;

        if (!point.TryGetProperty("timestamp", out var tsProp))
        {
            return false;
        }

        if (tsProp.ValueKind == JsonValueKind.String)
        {
            var raw = tsProp.GetString();
            if (string.IsNullOrWhiteSpace(raw))
            {
                return false;
            }

            // ISO 8601 forsøges først; fanger fremtidige Trendlog-formater.
            if (DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out timestamp))
            {
                return true;
            }

            if (DateTime.TryParseExact(raw, TimestampFormats, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var dt))
            {
                timestamp = new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc));
                return true;
            }
        }

        return false;
    }

    private static bool TryReadIntFromString(JsonElement point, string propertyName, out int parsed)
    {
        parsed = 0;

        if (!point.TryGetProperty(propertyName, out var element))
        {
            return false;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.Number:
                if (element.TryGetInt32(out parsed))
                {
                    return true;
                }
                if (element.TryGetDouble(out var d))
                {
                    parsed = (int)Math.Round(d, MidpointRounding.AwayFromZero);
                    return true;
                }
                return false;

            case JsonValueKind.String:
                var raw = element.GetString();
                if (string.IsNullOrWhiteSpace(raw))
                {
                    return false;
                }

                if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
                {
                    return true;
                }

                // Visse Trendlog-feeds kan returnere decimaler som strenge
                // (fx "12.5"); afrundes for at passe ind i int-kontrakten.
                if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var dv))
                {
                    parsed = (int)Math.Round(dv, MidpointRounding.AwayFromZero);
                    return true;
                }

                return false;

            default:
                return false;
        }
    }
}

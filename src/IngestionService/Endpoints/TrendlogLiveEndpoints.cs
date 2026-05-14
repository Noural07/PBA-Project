using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using IngestionService.Pipeline;
using IngestionService.Trendlog;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

namespace IngestionService.Endpoints;

/// <summary>
/// Live-endpoints der kalder det reelle Trendlog-API (Fase A). Endpoints
/// returnerer rå JSON, så udvikleren kan inspicere record-strukturen direkte
/// i Swagger og bekræfte, at points og operatør-kommentarer kommer ind, før
/// data normaliseres gennem pipelinen.
/// </summary>
public static class TrendlogLiveEndpoints
{
    /// <summary>
    /// Default-body til live-endpointet for kanal 20.
    /// <para>
    /// Empirisk verificeret i en separat node-server-implementation: <c>viewid=9668</c>
    /// er kanalens hovedvisning og skal anvendes på <em>alle</em> feeds — ikke kun
    /// stoptime. Tidligere antagelse om at <c>viewid=0</c> betød "standard-aggregering"
    /// for diff-feeds var forkert; Trendlog returnerer tomme points-arrays når
    /// kanal-specifik visning ikke er sat.
    /// </para>
    /// <para>
    /// Aggregeringsmetoden afviger pr. feed:
    /// <list type="bullet">
    ///   <item><description><c>stoptime</c>: <c>method=none</c> (rå event-points med kommentar pr. stop).</description></item>
    ///   <item><description><c>cnt</c>: <c>method=diff</c> (produktion pr. interval).</description></item>
    ///   <item><description><c>runtime</c>: <c>method=diff</c> (køretid pr. interval).</description></item>
    ///   <item><description><c>porder</c>: <c>method=none</c> (ordrekontekst som rå dict-comment).</description></item>
    /// </list>
    /// </para>
    /// </summary>
    public static readonly IReadOnlyList<TrendlogFeedRequest> Channel20DefaultFeeds = new[]
    {
        new TrendlogFeedRequest { FeedId = "XYZ01_stoptime", Method = "none", Noneg = false, ViewId = 9668 },
        new TrendlogFeedRequest { FeedId = "XYZ01_cnt",      Method = "diff", Noneg = false, ViewId = 9668 },
        new TrendlogFeedRequest { FeedId = "XYZ01_runtime",  Method = "diff", Noneg = false, ViewId = 9668 },
        new TrendlogFeedRequest { FeedId = "XYZ01_porder",   Method = "none", Noneg = false, ViewId = 9668 }
    };

    public static IEndpointRouteBuilder MapTrendlogLiveEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/ingestion/trendlog").WithTags("Trendlog (live)");

        group.MapPost("/probe", ProbeAsync)
            .WithName("ProbeTrendlog")
            .WithSummary("Rå POST-probe mod Trendlog – pass-through af bodyen.")
            .WithDescription(
                "Sender det modtagne JSON-array som body til Trendlog's " +
                "POST /V1/channels/{id}/feeds. Hvis bodyen er tom, anvendes en " +
                "default med XYZ01_stoptime (viewid 9668). Returnerer Trendlogs " +
                "rå svar inkl. kanal-metadata, feeds og points med comments.")
            .Accepts<List<TrendlogFeedRequest>>("application/json")
            .WithOpenApi();

        group.MapPost("/ingest-live", IngestLiveAsync)
            .WithName("IngestTrendlogLive")
            .WithSummary("Henter live-data fra Trendlog og kører pipelinen end-to-end.")
            .WithDescription(
                "Kalder Trendlog med default-bodyen for kanal 20 (alle 4 test-feeds), " +
                "mapper svaret til den interne TrendlogBatchPayload, kører " +
                "MeasurementNormalizer og publicerer MeasurementReceived + " +
                "OperatorCommentRegistered på RabbitMQ. Returnerer samme " +
                "aggregat-respons som /ingestion/simulate.")
            .WithOpenApi();

        return routes;
    }

    /// <summary>
    /// POST /ingestion/trendlog/probe?channelId=20&amp;daysBack=1
    /// med JSON-body som array af feed-anmodninger. Returnerer Trendlogs rå svar.
    /// </summary>
    private static async Task<IResult> ProbeAsync(
        [FromQuery] int? channelId,
        [FromQuery] int? daysBack,
        [FromBody] List<TrendlogFeedRequest>? body,
        ITrendlogClient trendlogClient,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("IngestionService.Endpoints.TrendlogProbe");
        var resolvedChannel = channelId is > 0 ? channelId.Value : 20;
        var resolvedDaysBack = daysBack is > 0 ? daysBack.Value : 1;

        var feedRequests = (body is null || body.Count == 0)
            ? (IReadOnlyCollection<TrendlogFeedRequest>)Channel20DefaultFeeds
            : body;

        try
        {
            var raw = await trendlogClient.GetFeedsRawAsync(
                resolvedChannel, resolvedDaysBack, feedRequests, cancellationToken);

            logger.LogInformation(
                "Trendlog probe ok. ChannelId={ChannelId} DaysBack={DaysBack} Feeds={Feeds}",
                resolvedChannel, resolvedDaysBack, feedRequests.Count);

            return Results.Ok(new
            {
                channelId = resolvedChannel,
                daysBack = resolvedDaysBack,
                fetchedAtUtc = DateTimeOffset.UtcNow,
                requestedFeeds = feedRequests,
                payload = raw
            });
        }
        catch (TrendlogApiException ex)
        {
            logger.LogWarning(ex,
                "Trendlog probe fejlede med status {Status}", ex.StatusCode);
            return Results.Problem(
                statusCode: ex.StatusCode == 0 ? StatusCodes.Status502BadGateway : ex.StatusCode,
                title: "Trendlog returnerede en fejl",
                detail: ex.Message,
                extensions: new Dictionary<string, object?>
                {
                    ["upstreamBody"] = ex.ResponseBody
                });
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
        catch (OperationCanceledException)
        {
            return Results.StatusCode(StatusCodes.Status499ClientClosedRequest);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Uventet fejl ved Trendlog probe");
            return Results.Problem(
                statusCode: StatusCodes.Status502BadGateway,
                title: "Kunne ikke nå Trendlog",
                detail: ex.Message);
        }
    }

    /// <summary>
    /// POST /ingestion/trendlog/ingest-live?channelId=20&amp;daysBack=1
    /// Henter live-data fra Trendlog, mapper til <see cref="TrendlogBatchPayload"/>,
    /// kører normaliseringen og publicerer events på RabbitMQ. Returnerer
    /// samme aggregat-respons som <c>/ingestion/simulate</c>.
    /// </summary>
    private static async Task<IResult> IngestLiveAsync(
        [FromQuery] int? channelId,
        [FromQuery] int? daysBack,
        ITrendlogClient trendlogClient,
        ITrendlogResponseMapper mapper,
        IMeasurementNormalizer normalizer,
        IngestionPublisher publisher,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("IngestionService.Endpoints.TrendlogIngestLive");
        var resolvedChannel = channelId is > 0 ? channelId.Value : 20;
        var resolvedDaysBack = daysBack is > 0 ? daysBack.Value : 1;

        // Tidsvinduet udledes deterministisk af daysBack: vinduet starter ved
        // midnat på den ældste dato (UTC) og slutter ved nuværende UTC-tidspunkt.
        // Dette giver et logisk sammenhængende interval, der kan publiceres som
        // WindowStart/WindowEnd på MeasurementReceived.
        var nowUtc = DateTimeOffset.UtcNow;
        var windowStart = new DateTimeOffset(
            nowUtc.UtcDateTime.Date.AddDays(-resolvedDaysBack), TimeSpan.Zero);
        var windowEnd = nowUtc;

        try
        {
            var raw = await trendlogClient.GetFeedsRawAsync(
                resolvedChannel,
                resolvedDaysBack,
                Channel20DefaultFeeds,
                cancellationToken);

            var payload = mapper.Map(raw, resolvedChannel, windowStart, windowEnd);
            var result = normalizer.Normalize(payload);
            await publisher.PublishAsync(result, cancellationToken);

            logger.LogInformation(
                "Live-ingest gennemført. ChannelId={ChannelId} DaysBack={DaysBack} "
                + "Stoptime={StopCount} Comments={CommentCount} TotalDowntimeMin={Downtime} "
                + "CorrelationId={CorrelationId}",
                resolvedChannel, resolvedDaysBack,
                payload.Feeds.Stoptime.Count, result.OperatorComments.Count,
                result.Measurement.TotalDowntimeMinutes, result.Measurement.CorrelationId);

            return Results.Accepted(value: new
            {
                correlationId = result.Measurement.CorrelationId,
                measurementEventId = result.Measurement.EventId,
                channelId = result.Measurement.ChannelId,
                windowStart = result.Measurement.WindowStart,
                windowEnd = result.Measurement.WindowEnd,
                producedUnits = result.Measurement.ProducedUnits,
                runtimeSeconds = result.Measurement.RuntimeSeconds,
                totalDowntimeMinutes = result.Measurement.TotalDowntimeMinutes,
                topReason = result.Measurement.TopReason,
                orderId = result.Measurement.OrderId,
                completionPct = result.Measurement.CompletionPct,
                stopReasonAggregates = result.Measurement.StopReasons,
                operatorComments = result.OperatorComments.Count,
                anomalies = result.DataAnomalies
            });
        }
        catch (TrendlogApiException ex)
        {
            logger.LogWarning(ex,
                "Live-ingest fejlede mod Trendlog med status {Status}", ex.StatusCode);
            return Results.Problem(
                statusCode: ex.StatusCode == 0 ? StatusCodes.Status502BadGateway : ex.StatusCode,
                title: "Trendlog returnerede en fejl",
                detail: ex.Message,
                extensions: new Dictionary<string, object?>
                {
                    ["upstreamBody"] = ex.ResponseBody
                });
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
        catch (OperationCanceledException)
        {
            return Results.StatusCode(StatusCodes.Status499ClientClosedRequest);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Uventet fejl ved live-ingest fra Trendlog");
            return Results.Problem(
                statusCode: StatusCodes.Status502BadGateway,
                title: "Kunne ikke gennemføre live-ingest",
                detail: ex.Message);
        }
    }
}

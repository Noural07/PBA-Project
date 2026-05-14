using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using IngestionService.Pipeline;
using IngestionService.Trendlog;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Pba.Shared.Contracts.V1;

namespace IngestionService.Endpoints;

/// <summary>
/// Ingestion-endpoints der eksponerer simulerede Trendlog-payloads til
/// pipelinen. Endpoints navngives med ekspliciterede ressource-stier
/// (<c>/ingestion/...</c>) således at YARP-routes kan videresende uden
/// at strippe prefix.
/// </summary>
public static class IngestionEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static IEndpointRouteBuilder MapIngestionEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/ingestion").WithTags("Ingestion");

        group.MapPost("/simulate", SimulateAsync)
            .WithName("SimulateTrendlogBatch")
            .WithOpenApi();

        group.MapPost("/stop-event", StopEventAsync)
            .WithName("PublishOperatorStopEvent")
            .WithOpenApi();

        group.MapGet("/sample-batch", LoadSampleBatchAsync)
            .WithName("ReadSampleBatch")
            .WithOpenApi();

        return routes;
    }

    /// <summary>
    /// POST /ingestion/simulate – modtager et Trendlog-shaped payload og
    /// kører hele normaliseringspipelinen efterfulgt af event-publicering.
    /// Hvis request-body er tom, indlæses <c>TestData/sample-batch.json</c>.
    /// </summary>
    private static async Task<IResult> SimulateAsync(
        HttpRequest request,
        IMeasurementNormalizer normalizer,
        IngestionPublisher publisher,
        IWebHostEnvironment environment,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("IngestionService.Endpoints.Simulate");

        TrendlogBatchPayload? payload;
        try
        {
            if (request.ContentLength is null or 0)
            {
                payload = await LoadFixtureAsync(environment, cancellationToken);
                logger.LogInformation("Simulator-endpoint kaldt uden body; indlæser sample-batch fra disk.");
            }
            else
            {
                payload = await JsonSerializer.DeserializeAsync<TrendlogBatchPayload>(
                    request.Body, JsonOptions, cancellationToken);
            }
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Ugyldig JSON modtaget på /ingestion/simulate");
            return Results.BadRequest(new { error = "Ugyldig JSON-payload.", detail = ex.Message });
        }

        if (payload is null)
        {
            return Results.BadRequest(new { error = "Tom eller manglende payload." });
        }

        var result = normalizer.Normalize(payload);
        await publisher.PublishAsync(result, cancellationToken);

        return Results.Accepted(value: new
        {
            correlationId = result.Measurement.CorrelationId,
            measurementEventId = result.Measurement.EventId,
            channelId = result.Measurement.ChannelId,
            totalDowntimeMinutes = result.Measurement.TotalDowntimeMinutes,
            topReason = result.Measurement.TopReason,
            orderId = result.Measurement.OrderId,
            completionPct = result.Measurement.CompletionPct,
            stopReasonAggregates = result.Measurement.StopReasons,
            operatorComments = result.OperatorComments.Count,
            anomalies = result.DataAnomalies
        });
    }

    /// <summary>
    /// POST /ingestion/stop-event – publicerer et enkelt
    /// <c>OperatorCommentRegistered</c>-event uden batch-aggregat.
    /// </summary>
    private static async Task<IResult> StopEventAsync(
        StopEventPayload payload,
        IngestionPublisher publisher,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("IngestionService.Endpoints.StopEvent");

        if (payload is null || string.IsNullOrWhiteSpace(payload.Reason))
        {
            return Results.BadRequest(new { error = "Feltet 'reason' er påkrævet." });
        }

        if (payload.DurationMinutes < 0)
        {
            return Results.BadRequest(new { error = "Feltet 'durationMinutes' må ikke være negativt." });
        }

        var comment = new OperatorCommentRegistered
        {
            ChannelId = payload.ChannelId,
            Timestamp = payload.Timestamp ?? DateTimeOffset.UtcNow,
            Reason = payload.Reason.Trim(),
            DurationMinutes = payload.DurationMinutes,
            OrderId = payload.OrderId
        };

        await publisher.PublishAsync(comment, cancellationToken);
        logger.LogInformation(
            "Stop-event accepteret. Reason='{Reason}' Duration={Duration} CorrelationId={CorrelationId}",
            comment.Reason, comment.DurationMinutes, comment.CorrelationId);

        return Results.Accepted(value: new
        {
            correlationId = comment.CorrelationId,
            stopEventId = comment.StopEventId,
            occurredAt = comment.OccurredAt
        });
    }

    /// <summary>
    /// GET /ingestion/sample-batch – returnerer det indbyggede sample-payload
    /// råt, så udviklere kan inspicere det i Swagger og tilrette egne tests.
    /// </summary>
    private static async Task<IResult> LoadSampleBatchAsync(
        IWebHostEnvironment environment,
        CancellationToken cancellationToken)
    {
        var payload = await LoadFixtureAsync(environment, cancellationToken);
        return Results.Ok(payload);
    }

    private static async Task<TrendlogBatchPayload> LoadFixtureAsync(
        IWebHostEnvironment environment, CancellationToken cancellationToken)
    {
        var fixturePath = Path.Combine(environment.ContentRootPath, "TestData", "sample-batch.json");
        if (!File.Exists(fixturePath))
        {
            throw new FileNotFoundException(
                $"Fixture-filen blev ikke fundet på stien {fixturePath}. " +
                "Kontroller, at TestData/sample-batch.json kopieres med servicens indhold.",
                fixturePath);
        }

        await using var stream = File.OpenRead(fixturePath);
        var payload = await JsonSerializer.DeserializeAsync<TrendlogBatchPayload>(
            stream, JsonOptions, cancellationToken);
        return payload ?? throw new InvalidOperationException("Sample-batch fixturen kunne ikke deserialiseres.");
    }
}

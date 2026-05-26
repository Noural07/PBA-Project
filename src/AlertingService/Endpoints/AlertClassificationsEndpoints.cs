using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AlertingService.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AlertingService.Endpoints;

/// <summary>
/// Endpoint der returnerer den fulde AI-klassifikationshistorik for én alarm fra <c>classified_stop_reasons</c>.
/// </summary>

public static class AlertClassificationsEndpoints
{
    public static IEndpointRouteBuilder MapAlertClassificationsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapGet("/alerts/{correlationId:guid}/classifications", HandleAsync)
            .WithName("AlertClassifications")
            .WithOpenApi();

        return endpoints;
    }

    private static async Task<IResult> HandleAsync(
        Guid correlationId,
        AlertingDbContext db,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("AlertClassifications");

        var rows = await db.ClassifiedStopReasons
            .AsNoTracking()
            .Where(c => c.CorrelationId == correlationId)
            .OrderBy(c => c.OccurredAt)
            .Select(c => new ClassificationDto(
                c.EventId,
                c.StopEventId,
                c.OccurredAt,
                c.ChannelId,
                c.OriginalReason,
                c.Category,
                c.Subcategory,
                c.StandardizedReason,
                c.Severity,
                c.RecommendedAction,
                c.Confidence,
                c.LatencyMs,
                c.IsFallback))
            .ToListAsync(cancellationToken);

        logger.LogInformation(
            "Audit-forespørgsel for CorrelationId={CorrelationId} returnerede {RowCount} klassifikationer.",
            correlationId, rows.Count);

        return Results.Ok(new
        {
            correlationId,
            count = rows.Count,
            classifications = rows
        });
    }

    /// <summary>
    /// Flad projektion af én række i <c>classified_stop_reasons</c>. Bevidst
    /// fri af EF-tracking og uden navigations-properties; sikrer dermed at
    /// JSON-payload'en er stabil og uafhængig af DbContext'ens lifecycle.
    /// </summary>
    private sealed record ClassificationDto(
        Guid EventId,
        Guid StopEventId,
        DateTimeOffset OccurredAt,
        int ChannelId,
        string OriginalReason,
        string Category,
        string Subcategory,
        string StandardizedReason,
        string Severity,
        string RecommendedAction,
        double Confidence,
        long LatencyMs,
        bool IsFallback);
}

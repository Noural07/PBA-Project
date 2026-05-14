using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AnalyzerService.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace AnalyzerService.Endpoints;

/// <summary>
/// Inspektions-endpoints til <c>AnalyzerService</c>. Endpointene er bevidst
/// tynde projektioner over EF Core-aggregaterne og er udelukkende beregnet
/// til demonstration, manuel verifikation under udvikling og rapportering.
/// I produktion forventes læsemodellen at blive eksponeret via
/// <c>AlertingService</c> eller en dedikeret read-API.
/// </summary>
public static class AnalyzerEndpoints
{
    public static IEndpointRouteBuilder MapAnalyzerEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/measurements").WithTags("Analyzer");

        group.MapGet("/", ListMeasurementsAsync)
            .WithName("ListMeasurements")
            .WithOpenApi();

        group.MapGet("/{id:guid}", GetMeasurementByIdAsync)
            .WithName("GetMeasurementById")
            .WithOpenApi();

        routes.MapGet("/critical-alerts", ListCriticalAlertsAsync)
            .WithTags("Analyzer")
            .WithName("ListCriticalAlerts")
            .WithOpenApi();

        return routes;
    }

    private static async Task<IResult> ListMeasurementsAsync(
        AnalyzerDbContext db,
        string? orderId,
        int? channelId,
        int? take,
        CancellationToken cancellationToken)
    {
        var pageSize = take is null or <= 0 ? 50 : Math.Min(take.Value, 200);

        var query = db.Measurements
            .AsNoTracking()
            .Include(m => m.StopEvents)
            .Include(m => m.CriticalAlert)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(orderId))
        {
            query = query.Where(m => m.OrderId == orderId);
        }

        if (channelId is { } cid)
        {
            query = query.Where(m => m.ChannelId == cid);
        }

        var rows = await query
            .OrderByDescending(m => m.ReceivedAt)
            .Take(pageSize)
            .Select(m => new
            {
                m.Id,
                m.CorrelationId,
                m.ChannelId,
                m.WindowStart,
                m.WindowEnd,
                m.ProducedUnits,
                m.RuntimeSeconds,
                m.TotalDowntimeMinutes,
                m.TopReason,
                m.OrderId,
                m.OrderTarget,
                m.CompletionPct,
                m.IsCritical,
                m.ReceivedAt,
                StopEvents = m.StopEvents.Select(s => new
                {
                    s.Reason,
                    s.DurationMinutes,
                    s.Occurrences,
                    s.IsCriticalReason
                }),
                Alert = m.CriticalAlert == null ? null : new
                {
                    m.CriticalAlert.Id,
                    m.CriticalAlert.Severity,
                    m.CriticalAlert.Rule,
                    m.CriticalAlert.TriggeredRules,
                    m.CriticalAlert.Description,
                    m.CriticalAlert.ObservedCriticalReasons
                }
            })
            .ToListAsync(cancellationToken);

        return Results.Ok(new { count = rows.Count, items = rows });
    }

    private static async Task<IResult> GetMeasurementByIdAsync(
        Guid id,
        AnalyzerDbContext db,
        CancellationToken cancellationToken)
    {
        var measurement = await db.Measurements
            .AsNoTracking()
            .Include(m => m.StopEvents)
            .Include(m => m.CriticalAlert)
            .FirstOrDefaultAsync(m => m.Id == id, cancellationToken);

        return measurement is null ? Results.NotFound() : Results.Ok(measurement);
    }

    private static async Task<IResult> ListCriticalAlertsAsync(
        AnalyzerDbContext db,
        int? take,
        CancellationToken cancellationToken)
    {
        var pageSize = take is null or <= 0 ? 50 : Math.Min(take.Value, 200);

        var rows = await db.CriticalAlerts
            .AsNoTracking()
            .OrderByDescending(c => c.CreatedAt)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return Results.Ok(new { count = rows.Count, items = rows });
    }
}

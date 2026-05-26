using System;
using System.Globalization;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using AlertingService.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AlertingService.Endpoints;

/// <summary>
/// Eksponerer <c>GET /alerts/stream</c> som SSE — sender snapshot ved tilkobling og skubber nye alarmer i realtid.
/// </summary>

public static class AlertStreamEndpoints
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = false
    };

    public static IEndpointRouteBuilder MapAlertStreamEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/alerts/stream", HandleStreamAsync)
            .WithName("AlertStream")
            .WithOpenApi();

        return endpoints;
    }

    private static async Task HandleStreamAsync(
        HttpContext context,
        AlertStore store,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("AlertStream");

        context.Response.Headers.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache, no-store";
        context.Response.Headers.Connection = "keep-alive";
        context.Response.Headers["X-Accel-Buffering"] = "no";

        await context.Response.Body.FlushAsync(cancellationToken);

        // Skriv aktuel ring-buffer som initial snapshot.
        var snapshot = store.Snapshot();
        foreach (var alert in snapshot)
        {
            await WriteEventAsync(context, "snapshot", alert, cancellationToken);
        }

        var (reader, subscription) = store.Subscribe();
        using (subscription)
        {
            // Periodisk keep-alive (kommentar-event), så proxies/loadbalancere
            // ikke timeoutter en stille SSE-forbindelse.
            using var keepAlive = new PeriodicTimer(TimeSpan.FromSeconds(15));

            // readTask og keepAliveTask hejses ud af løkken og genoprettes først når de vinder Task.WhenAny.
            // (1) ReadAsync må ikke afløses før dens værdi er konsumeret — alarm-leverance kan gå tabt.
            // (2) WaitForNextTickAsync må ikke kaldes mens et kald er pending — kaster InvalidOperationException.
           
            var readTask = reader.ReadAsync(cancellationToken).AsTask();
            var keepAliveTask = keepAlive.WaitForNextTickAsync(cancellationToken).AsTask();

            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    var completed = await Task.WhenAny(readTask, keepAliveTask);

                    if (completed == readTask)
                    {
                        var alert = await readTask;
                        await WriteEventAsync(context, "alert", alert, cancellationToken);
                        readTask = reader.ReadAsync(cancellationToken).AsTask();
                    }
                    else
                    {
                        // Observér resultatet (kan kaste OperationCanceledException
                        // ved nedlukning – fanges af catch-blokken nedenfor).
                        await keepAliveTask;
                        await context.Response.WriteAsync(": keep-alive\n\n", cancellationToken);
                        await context.Response.Body.FlushAsync(cancellationToken);
                        keepAliveTask = keepAlive.WaitForNextTickAsync(cancellationToken).AsTask();
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Klienten lukkede forbindelsen – normal afslutning.
            }
            catch (ChannelClosedException)
            {
                // AlertStore terminerede subscriptionen – normal afslutning.
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "SSE-stream afsluttede uventet.");
            }
        }
    }

    private static async Task WriteEventAsync(
        HttpContext context,
        string eventName,
        object payload,
        CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(payload, Json);
        var frame = string.Create(
            CultureInfo.InvariantCulture,
            $"event: {eventName}\ndata: {json}\n\n");

        await context.Response.WriteAsync(frame, cancellationToken);
        await context.Response.Body.FlushAsync(cancellationToken);
    }
}

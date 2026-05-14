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
/// Eksponerer <c>GET /alerts/stream</c> som en HTTP/1.1 Server-Sent-Events-
/// kanal, der tildeler hver tilkoblet klient et øjeblikkeligt snapshot af de
/// seneste konsoliderede alarmer (op til <see cref="AlertStore"/>'s kapacitet)
/// og dernæst skubber alle nye alarmer i realtid.
/// </summary>
/// <remarks>
/// <para>
/// SSE er bevidst valgt frem for WebSockets eller direkte RabbitMQ-eksponering.
/// SSE er en standard del af enhver moderne browser, kræver ingen
/// klient-bibliotek og er one-way, hvilket matcher kontrakten
/// "AlertingService -> Frontend" perfekt. Ved at lade <c>AlertingService</c>
/// abstrahere broker'en undgås tæt kobling mellem browser og RabbitMQ, og
/// frontend'en behøver ingen credentials til broker-klyngen.
/// </para>
/// <para>
/// Endpointet er ikke autentificeret i Phase 4 og er udelukkende beregnet
/// til lokal demonstration. CORS-policyen begrænser dog hvilken origin der
/// kan koble sig på (jf. <c>ALERTING_FRONTEND_ORIGIN</c>).
/// </para>
/// </remarks>
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

            // BÅDE readTask OG keepAliveTask hejses ud af løkken og
            // genoprettes først, når de selv vinder Task.WhenAny. Mønstret er
            // krævet for to ortogonale grunde:
            //   (1) ChannelReader.ReadAsync må ikke afløses, før den læser ér
            //       konsumeret, ellers kan en alarm-leverance gå tabt.
            //   (2) PeriodicTimer.WaitForNextTickAsync MÅ ikke kaldes mens et
            //       tidligere kald stadig er pending; ellers kastes
            //       InvalidOperationException ("Operation is not valid …").
            //       Bug observeret i Phase 4-røg-test 2026-04-30.
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

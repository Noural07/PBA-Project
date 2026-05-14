using System.Net.Sockets;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Pba.Shared.HealthChecks;

/// <summary>
/// Letvægts AMQP-health check, der verificerer, at RabbitMQ-broker'ens TCP-port
/// kan etablere en forbindelse. Anvendes i Phase 1, hvor selve AMQP-klienten
/// endnu ikke er introduceret. Senere faser kan udskifte denne check med en
/// fuld AMQP-handshake-baseret variant uden at ændre kontrakten mod Gatus.
/// </summary>
public sealed class RabbitMqTcpHealthCheck : IHealthCheck
{
    private readonly string _host;
    private readonly int _port;
    private readonly TimeSpan _timeout;

    public RabbitMqTcpHealthCheck(string host, int port, TimeSpan? timeout = null)
    {
        _host = host;
        _port = port;
        _timeout = timeout ?? TimeSpan.FromSeconds(2);
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var tcp = new TcpClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(_timeout);

            await tcp.ConnectAsync(_host, _port, cts.Token).ConfigureAwait(false);

            return tcp.Connected
                ? HealthCheckResult.Healthy($"RabbitMQ AMQP port {_host}:{_port} accepts TCP connections.")
                : HealthCheckResult.Unhealthy($"RabbitMQ AMQP port {_host}:{_port} did not respond.");
        }
        catch (OperationCanceledException)
        {
            return HealthCheckResult.Unhealthy(
                $"RabbitMQ AMQP probe to {_host}:{_port} timed out after {_timeout.TotalSeconds:F0}s.");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy(
                $"RabbitMQ AMQP probe to {_host}:{_port} failed.", ex);
        }
    }
}

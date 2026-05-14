using System;
using System.Threading.Tasks;
using AlertingService.Domain;
using MassTransit;
using Microsoft.Extensions.Logging;
using Pba.Shared.Contracts.V1;

namespace AlertingService.Consumers;

/// <summary>
/// Konsumerer <see cref="CriticalAlertTriggered"/>-events fra RabbitMQ og
/// indfører dem i den in-memory ring-buffer. Hver alarm logges på
/// <c>Warning</c>-niveau med strukturerede felter, så Loki/Grafana kan
/// alertere via en LogQL-query af typen
/// <c>{service="alerting-service"} | json | severity="High"</c>.
/// </summary>
public sealed class CriticalAlertTriggeredConsumer : IConsumer<CriticalAlertTriggered>
{
    private readonly AlertStore _store;
    private readonly ILogger<CriticalAlertTriggeredConsumer> _logger;

    public CriticalAlertTriggeredConsumer(
        AlertStore store,
        ILogger<CriticalAlertTriggeredConsumer> logger)
    {
        _store = store;
        _logger = logger;
    }

    public Task Consume(ConsumeContext<CriticalAlertTriggered> context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var message = context.Message;

        using var correlationScope = Serilog.Context.LogContext.PushProperty(
            "correlationId", message.CorrelationId);
        using var alertScope = Serilog.Context.LogContext.PushProperty(
            "alertId", message.AlertId);

        var consolidated = _store.Upsert(message);

        // Højprioritets-strukturlog – falder ind i Loki som JSON og kan
        // matches i Grafana via dedikerede paneler i alert-dashboardet.
        _logger.LogWarning(
            "ConsolidatedAlert opdateret (kritisk-alarm). AlertId={AlertId} Severity={Severity} "
                + "Rule={Rule} Channel={ChannelId} TotalDowntimeMin={Downtime} TopReason='{TopReason}' "
                + "AiCategory={AiCategory} AiConfidence={AiConfidence}",
            consolidated.AlertId, consolidated.Severity, consolidated.Rule,
            consolidated.ChannelId, consolidated.TotalDowntimeMinutes,
            consolidated.TopReason ?? "-",
            consolidated.AiCategory ?? "-",
            consolidated.AiConfidence ?? double.NaN);

        return Task.CompletedTask;
    }
}

using System;
using System.Linq;
using System.Threading.Tasks;
using AnalyzerService.Domain;
using AnalyzerService.Persistence;
using AnalyzerService.Rules;
using MassTransit;
using Microsoft.Extensions.Logging;
using Pba.Shared.Contracts.V1;

namespace AnalyzerService.Consumers;

/// <summary>
/// Konsumerer <see cref="MeasurementReceived"/>-events fra RabbitMQ. Pr.
/// event:
/// <list type="number">
///   <item><description>Beriger logningen med <c>CorrelationId</c> via Serilog <c>LogContext</c>.</description></item>
///   <item><description>Evaluerer kritikalitetsreglerne via <see cref="CriticalRuleEvaluator"/>.</description></item>
///   <item><description>Persisterer målingen, dens stopårsager og evt. <see cref="CriticalAlert"/> i én transaktion.</description></item>
///   <item><description>Publicerer <see cref="CriticalAlertTriggered"/> hvis evalueringen var kritisk.</description></item>
/// </list>
/// </summary>
public sealed class MeasurementReceivedConsumer : IConsumer<MeasurementReceived>
{
    private readonly AnalyzerDbContext _db;
    private readonly CriticalRuleEvaluator _evaluator;
    private readonly ILogger<MeasurementReceivedConsumer> _logger;

    public MeasurementReceivedConsumer(
        AnalyzerDbContext db,
        CriticalRuleEvaluator evaluator,
        ILogger<MeasurementReceivedConsumer> logger)
    {
        _db = db;
        _evaluator = evaluator;
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<MeasurementReceived> context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var message = context.Message;

        // Eksplicit fuldt-kvalificeret reference: 'using MassTransit;' bringer
        // også en LogContext-type i scope, hvorfor det fulde Serilog-namespace
        // anvendes for at undgå CS0104.
        using var correlationScope = Serilog.Context.LogContext.PushProperty(
            "correlationId", message.CorrelationId);

        _logger.LogInformation(
            "MeasurementReceived modtaget. EventId={EventId} Kanal={ChannelId} TotalDowntimeMin={Downtime} OrderId={OrderId}",
            message.EventId, message.ChannelId, message.TotalDowntimeMinutes, message.OrderId ?? "-");

        var evaluation = _evaluator.Evaluate(message);

        var measurement = new Measurement
        {
            CorrelationId = message.CorrelationId,
            ChannelId = message.ChannelId,
            WindowStart = message.WindowStart,
            WindowEnd = message.WindowEnd,
            ProducedUnits = message.ProducedUnits,
            RuntimeSeconds = message.RuntimeSeconds,
            TotalDowntimeMinutes = message.TotalDowntimeMinutes,
            TopReason = message.TopReason,
            OrderId = message.OrderId,
            OrderTarget = message.OrderTarget,
            CompletionPct = message.CompletionPct,
            IsCritical = evaluation.IsCritical
        };

        foreach (var aggregate in message.StopReasons)
        {
            measurement.StopEvents.Add(new StopEvent
            {
                Reason = aggregate.Reason,
                DurationMinutes = aggregate.DurationMinutes,
                Occurrences = aggregate.Occurrences,
                IsCriticalReason = _evaluator.IsCriticalReason(aggregate.Reason)
            });
        }

        CriticalAlert? alert = null;
        if (evaluation.IsCritical)
        {
            alert = new CriticalAlert
            {
                MeasurementId = measurement.Id,
                CorrelationId = message.CorrelationId,
                Severity = "High",
                Rule = evaluation.PrimaryRule ?? "Unknown",
                TriggeredRules = string.Join(",", evaluation.TriggeredRules),
                Description = evaluation.Description,
                TotalDowntimeMinutes = message.TotalDowntimeMinutes,
                TopReason = message.TopReason,
                ObservedCriticalReasons = evaluation.ObservedCriticalReasons.Count > 0
                    ? string.Join(",", evaluation.ObservedCriticalReasons)
                    : null
            };
            measurement.CriticalAlert = alert;
        }

        _db.Measurements.Add(measurement);
        await _db.SaveChangesAsync(context.CancellationToken);

        _logger.LogInformation(
            "Måling persisteret. MeasurementId={MeasurementId} IsCritical={IsCritical} StopEvents={StopCount}",
            measurement.Id, measurement.IsCritical, measurement.StopEvents.Count);

        if (alert is not null)
        {
            var criticalEvent = new CriticalAlertTriggered
            {
                CorrelationId = message.CorrelationId,
                AlertId = alert.Id,
                MeasurementId = measurement.Id,
                ChannelId = measurement.ChannelId,
                Severity = alert.Severity,
                Rule = alert.Rule,
                Description = alert.Description,
                TotalDowntimeMinutes = measurement.TotalDowntimeMinutes,
                TopReason = measurement.TopReason,
                OrderId = measurement.OrderId,
                CompletionPct = measurement.CompletionPct,
                ObservedCriticalReasons = evaluation.ObservedCriticalReasons.ToList()
            };

            await context.Publish(criticalEvent, context.CancellationToken);
            _logger.LogWarning(
                "CriticalAlertTriggered publiceret. AlertId={AlertId} Rule={Rule} TriggeredRules={TriggeredRules}",
                alert.Id, alert.Rule, alert.TriggeredRules);
        }
        else
        {
            _logger.LogInformation(
                "Måling vurderet ikke-kritisk. MeasurementId={MeasurementId}", measurement.Id);
        }
    }
}

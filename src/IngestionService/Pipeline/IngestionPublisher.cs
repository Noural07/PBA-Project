using System;
using System.Threading;
using System.Threading.Tasks;
using MassTransit;
using Microsoft.Extensions.Logging;
using Pba.Shared.Contracts.V1;

namespace IngestionService.Pipeline;

/// <summary>
/// Tynd indpakning omkring <see cref="IPublishEndpoint"/>, der publicerer
/// både batch-aggregatet og de individuelle operatørkommentarer på RabbitMQ
/// via MassTransit. Holdes adskilt fra <see cref="MeasurementNormalizer"/>
/// for at bevare ren separation mellem domænelogik og infrastruktur.
/// </summary>
public sealed class IngestionPublisher
{
    private readonly IPublishEndpoint _publishEndpoint;
    private readonly ILogger<IngestionPublisher> _logger;

    public IngestionPublisher(
        IPublishEndpoint publishEndpoint,
        ILogger<IngestionPublisher> logger)
    {
        _publishEndpoint = publishEndpoint;
        _logger = logger;
    }

    public async Task PublishAsync(NormalizationResult result, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);

        await _publishEndpoint.Publish(result.Measurement, cancellationToken);
        _logger.LogInformation(
            "Event publiceret: MeasurementReceived MeasurementEventId={EventId} Kanal={ChannelId} "
                + "TotalDowntimeMin={Downtime} CorrelationId={CorrelationId}",
            result.Measurement.EventId, result.Measurement.ChannelId,
            result.Measurement.TotalDowntimeMinutes, result.Measurement.CorrelationId);

        foreach (var comment in result.OperatorComments)
        {
            await _publishEndpoint.Publish(comment, cancellationToken);
        }

        if (result.OperatorComments.Count > 0)
        {
            _logger.LogInformation(
                "Event publiceret: {Count} OperatorCommentRegistered-events CorrelationId={CorrelationId}",
                result.OperatorComments.Count, result.Measurement.CorrelationId);
        }
    }

    public async Task PublishAsync(OperatorCommentRegistered comment, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(comment);

        await _publishEndpoint.Publish(comment, cancellationToken);
        _logger.LogInformation(
            "Event publiceret: OperatorCommentRegistered StopEventId={StopEventId} Reason='{Reason}' "
                + "DurationMinutes={Duration} CorrelationId={CorrelationId}",
            comment.StopEventId, comment.Reason, comment.DurationMinutes, comment.CorrelationId);
    }
}

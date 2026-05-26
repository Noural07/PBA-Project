using System;
using System.Threading.Tasks;
using AiService.Gemini;
using MassTransit;
using Microsoft.Extensions.Logging;
using Pba.Shared.Contracts.V1;

namespace AiService.Consumers;

/// <summary>
/// Konsumerer <see cref="OperatorCommentRegistered"/>-events og publicerer
/// et <see cref="StopReasonClassified"/>-event efter Gemini-klassifikation.
/// </summary>
public sealed class OperatorCommentRegisteredConsumer : IConsumer<OperatorCommentRegistered>
{
    private readonly IGeminiClassifier _classifier;
    private readonly ILogger<OperatorCommentRegisteredConsumer> _logger;

    public OperatorCommentRegisteredConsumer(
        IGeminiClassifier classifier,
        ILogger<OperatorCommentRegisteredConsumer> logger)
    {
        _classifier = classifier;
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<OperatorCommentRegistered> context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var message = context.Message;

        using var correlationScope = Serilog.Context.LogContext.PushProperty(
            "correlationId", message.CorrelationId);
        using var stopEventScope = Serilog.Context.LogContext.PushProperty(
            "stopEventId", message.StopEventId);

        _logger.LogInformation(
            "OperatorCommentRegistered modtaget. StopEventId={StopEventId} Kanal={ChannelId} "
                + "TrendlogCategory={TrendlogCategory} Reason='{Reason}' DurationMinutes={Duration}",
            message.StopEventId, message.ChannelId, message.Category ?? "n/a",
            message.Reason, message.DurationMinutes);

        var classification = await _classifier.ClassifyAsync(
            message.Reason,
            message.Category,
            message.DurationMinutes,
            context.CancellationToken);

        _logger.LogInformation(
            "Stopårsag klassificeret. Category={Category} Subcategory={Subcategory} "
                + "Severity={Severity} StandardizedReason='{Standardized}' "
                + "Confidence={Confidence:F2} LatencyMs={LatencyMs} IsFallback={IsFallback}",
            classification.Category,
            classification.Subcategory,
            classification.Severity,
            classification.StandardizedReason,
            classification.Confidence,
            classification.LatencyMs,
            classification.IsFallback);

        var classified = new StopReasonClassified
        {
            CorrelationId = message.CorrelationId,
            StopEventId = message.StopEventId,
            ChannelId = message.ChannelId,
            OriginalReason = message.Reason,
            Category = classification.Category,
            Subcategory = classification.Subcategory,
            StandardizedReason = classification.StandardizedReason,
            Severity = classification.Severity,
            RecommendedAction = classification.RecommendedAction,
            Confidence = classification.Confidence,
            LatencyMs = classification.LatencyMs,
            IsFallback = classification.IsFallback
        };

        await context.Publish(classified, context.CancellationToken);

        _logger.LogInformation(
            "StopReasonClassified publiceret. EventId={EventId} StopEventId={StopEventId} "
                + "Category={Category} Severity={Severity}",
            classified.EventId, classified.StopEventId,
            classified.Category, classified.Severity);
    }
}

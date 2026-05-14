using System;
using System.Threading.Tasks;
using AiService.Gemini;
using MassTransit;
using Microsoft.Extensions.Logging;
using Pba.Shared.Contracts.V1;

namespace AiService.Consumers;

/// <summary>
/// Konsumerer <see cref="OperatorCommentRegistered"/>-events fra RabbitMQ,
/// kalder Gemini-klassifikatoren via en <see cref="System.Net.Http.IHttpClientFactory"/>-
/// styret klient og publicerer det resulterende
/// <see cref="StopReasonClassified"/>-event tilbage til broker'en.
/// </summary>
/// <remarks>
/// <para>
/// Konsumenten bevarer <c>CorrelationId</c> uændret hele vejen igennem, så
/// <see cref="AlertingService"/> kan korrelere AI-klassifikationen med en
/// kritisk alarm der måtte være udløst i samme tidsvindue.
/// </para>
/// <para>
/// <b>Idempotens.</b> Idempotens håndteres på to niveauer: (1) MassTransit's
/// indbyggede retry-pipeline sikrer at en transient broker-fejl genleverer
/// samme message-id, og (2) AlertingService' Postgres-tabel anvender
/// <c>StopEventId</c> som unik nøgle, så et duplikat genprodukt fra Gemini
/// ikke skaber dobbeltrækker. Konsumenten i sig selv er bevidst stateløs.
/// </para>
/// <para>
/// <b>Fallback-strategi.</b> Hvis <see cref="IGeminiClassifier"/> efter alle
/// Polly-retries returnerer en fallback (markeret med
/// <see cref="GeminiClassificationResult.IsFallback"/>), publiceres alligevel
/// et <see cref="StopReasonClassified"/>-event med
/// <c>Category="Unclassified"</c>. Dette sikrer at downstream-frontend'en
/// aldrig blokeres af et utilgængeligt AI-lag — designkravet er, at en
/// midlertidig udfald af Gemini ikke må gøre pipelinen "Unhealthy".
/// </para>
/// </remarks>
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

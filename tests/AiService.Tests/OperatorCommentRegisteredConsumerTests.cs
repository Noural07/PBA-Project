using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AiService.Consumers;
using AiService.Gemini;
using FluentAssertions;
using MassTransit;
using MassTransit.Testing;
using Microsoft.Extensions.DependencyInjection;
using Pba.Shared.Contracts.V1;
using Xunit;

namespace AiService.Tests;

/// <summary>
/// Integration-test der verificerer fase C-flowet
/// <c>OperatorCommentRegistered → IGeminiClassifier → StopReasonClassified</c>
/// ende-til-ende på MassTransit's in-memory test-harness. Testene anvender
/// en stub-implementation af <see cref="IGeminiClassifier"/>, så Gemini's
/// faktiske ikke-deterministiske svar holdes uden for test-domænet — i tråd
/// med fase C-promptens overvejelse om determinisme i AI-tests.
/// </summary>
public sealed class OperatorCommentRegisteredConsumerTests
{
    [Fact(DisplayName = "Konsumenten publicerer StopReasonClassified med fuld mapping ved gyldig klassifikation")]
    public async Task Consume_PublishesStopReasonClassified_WithCompleteMapping()
    {
        // Arrange
        var fixedClassification = new GeminiClassificationResult(
            Category: "Fault",
            Subcategory: "Mekanisk havari",
            StandardizedReason: "Maskinhavari på pakkelinje",
            Severity: "High",
            RecommendedAction: "Tilkald maskinmester",
            Confidence: 0.91,
            LatencyMs: 412L,
            IsFallback: false);

        await using var provider = BuildProvider(new StubClassifier(fixedClassification));
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        var stopEventId = Guid.NewGuid();
        var correlationId = Guid.NewGuid();
        var inbound = new OperatorCommentRegistered
        {
            CorrelationId = correlationId,
            StopEventId = stopEventId,
            ChannelId = 20,
            Timestamp = DateTimeOffset.UtcNow,
            Reason = "Pakkemaskine smed papir og blokerede båndet",
            Category = "Fault",
            DurationMinutes = 14
        };

        // Act
        await harness.Bus.Publish(inbound);

        // Assert
        (await harness.Consumed.Any<OperatorCommentRegistered>()).Should().BeTrue();
        (await harness.Published.Any<StopReasonClassified>()).Should().BeTrue();

        var published = harness.Published.Select<StopReasonClassified>().Single();
        var message = published.Context.Message;

        message.CorrelationId.Should().Be(correlationId);
        message.StopEventId.Should().Be(stopEventId);
        message.ChannelId.Should().Be(20);
        message.OriginalReason.Should().Be(inbound.Reason);
        message.Category.Should().Be("Fault");
        message.Subcategory.Should().Be("Mekanisk havari");
        message.StandardizedReason.Should().Be("Maskinhavari på pakkelinje");
        message.Severity.Should().Be("High");
        message.RecommendedAction.Should().Be("Tilkald maskinmester");
        message.Confidence.Should().BeApproximately(0.91, 0.0001);
        message.LatencyMs.Should().Be(412L);
        message.IsFallback.Should().BeFalse();
    }

    [Fact(DisplayName = "Konsumenten publicerer Unclassified-fallback når klassifikatoren markerer fallback")]
    public async Task Consume_FallbackClassification_PublishesUnclassifiedReason()
    {
        // Arrange
        var fallbackResult = new GeminiClassificationResult(
            Category: "Unclassified",
            Subcategory: "Ukategoriseret",
            StandardizedReason: "Ukendt fejl på linje",
            Severity: "Low",
            RecommendedAction: "Kræver manuel gennemgang",
            Confidence: 0.0,
            LatencyMs: 0L,
            IsFallback: true);

        await using var provider = BuildProvider(new StubClassifier(fallbackResult));
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        // Act
        await harness.Bus.Publish(new OperatorCommentRegistered
        {
            CorrelationId = Guid.NewGuid(),
            StopEventId = Guid.NewGuid(),
            ChannelId = 20,
            Timestamp = DateTimeOffset.UtcNow,
            Reason = "Ukendt fejl på linje",
            Category = null,
            DurationMinutes = 7
        });

        // Assert
        (await harness.Published.Any<StopReasonClassified>()).Should().BeTrue();
        var message = harness.Published.Select<StopReasonClassified>().Single().Context.Message;

        message.Category.Should().Be("Unclassified");
        message.IsFallback.Should().BeTrue();
        message.RecommendedAction.Should().Be("Kræver manuel gennemgang");
    }

    [Fact(DisplayName = "Konsumenten viderefører Trendlog-grov-kategori til klassifikatoren")]
    public async Task Consume_PassesTrendlogCategoryAsContext()
    {
        // Arrange
        var spy = new SpyClassifier(new GeminiClassificationResult(
            Category: "Maintenance",
            Subcategory: "Planlagt rengøring",
            StandardizedReason: "Rengøring af linje",
            Severity: "Low",
            RecommendedAction: "Genoptag produktion efter rengøring",
            Confidence: 0.95,
            LatencyMs: 280L,
            IsFallback: false));

        await using var provider = BuildProvider(spy);
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        // Act
        await harness.Bus.Publish(new OperatorCommentRegistered
        {
            CorrelationId = Guid.NewGuid(),
            StopEventId = Guid.NewGuid(),
            ChannelId = 20,
            Timestamp = DateTimeOffset.UtcNow,
            Reason = "Rengøring af linje",
            Category = "Maintenance",
            DurationMinutes = 18
        });

        // Assert
        (await harness.Consumed.Any<OperatorCommentRegistered>()).Should().BeTrue();
        spy.LastTrendlogCategory.Should().Be("Maintenance");
        spy.LastReason.Should().Be("Rengøring af linje");
        spy.LastDurationMinutes.Should().Be(18);
    }

    private static ServiceProvider BuildProvider(IGeminiClassifier classifier)
    {
        var services = new ServiceCollection();
        services.AddSingleton(classifier);
        services.AddLogging();

        services.AddMassTransitTestHarness(cfg =>
        {
            cfg.AddConsumer<OperatorCommentRegisteredConsumer>();
        });

        return services.BuildServiceProvider(true);
    }

    /// <summary>Stub der altid returnerer en fast klassifikation.</summary>
    private sealed class StubClassifier : IGeminiClassifier
    {
        private readonly GeminiClassificationResult _fixed;

        public StubClassifier(GeminiClassificationResult fixedResult)
        {
            _fixed = fixedResult;
        }

        public Task<GeminiClassificationResult> ClassifyAsync(
            string reason, string? trendlogCategory, int durationMinutes,
            CancellationToken cancellationToken)
            => Task.FromResult(_fixed);
    }

    /// <summary>
    /// Spy der gemmer det sidste sæt argumenter, så tests kan bekræfte at
    /// konsumenten viderefører kontekstfelter (kategori, varighed, fri-tekst).
    /// </summary>
    private sealed class SpyClassifier : IGeminiClassifier
    {
        private readonly GeminiClassificationResult _fixed;

        public SpyClassifier(GeminiClassificationResult fixedResult)
        {
            _fixed = fixedResult;
        }

        public string? LastReason { get; private set; }

        public string? LastTrendlogCategory { get; private set; }

        public int LastDurationMinutes { get; private set; }

        public Task<GeminiClassificationResult> ClassifyAsync(
            string reason, string? trendlogCategory, int durationMinutes,
            CancellationToken cancellationToken)
        {
            LastReason = reason;
            LastTrendlogCategory = trendlogCategory;
            LastDurationMinutes = durationMinutes;
            return Task.FromResult(_fixed);
        }
    }
}

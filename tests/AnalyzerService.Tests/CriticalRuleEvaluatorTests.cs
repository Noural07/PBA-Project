using System;
using System.Collections.Generic;
using AnalyzerService.Rules;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Pba.Shared.Contracts.V1;
using Xunit;

namespace AnalyzerService.Tests;

/// <summary>
/// Enhedstest for <see cref="CriticalRuleEvaluator"/>. Klassen er
/// regelmotorens centrale forretningslogik og isoleres her fra både
/// HTTP-, RabbitMQ- og PostgreSQL-afhængigheder, hvilket muliggør hurtig
/// og deterministisk test af de tre kritikalitetsregler:
/// <c>DowntimeExceedsThreshold</c>, <c>CriticalReasonObserved</c> og
/// <c>OrderCompletionBelowThreshold</c>.
/// </summary>
/// <remarks>
/// Testene følger Arrange–Act–Assert mønstret. Tærskler injiceres via
/// <see cref="IOptions{T}"/>, så hver test kan eksponere den konkrete
/// konfiguration den hviler på, og kan eksekveres parallelt uden delt
/// tilstand.
/// </remarks>
public sealed class CriticalRuleEvaluatorTests
{
    private static CriticalRuleEvaluator CreateSut(CriticalRuleOptions? overrides = null)
    {
        var options = overrides ?? new CriticalRuleOptions
        {
            TotalDowntimeThresholdMinutes = 30,
            MinimumCompletionPct = 50.0,
            CriticalReasonKeywords =
            [
                "Banestyringsfejl",
                "Papirbrud",
                "Nødstop",
                "El-fejl"
            ]
        };

        return new CriticalRuleEvaluator(Options.Create(options));
    }

    private static MeasurementReceived BuildMeasurement(
        int channelId = 20,
        int totalDowntimeMinutes = 0,
        double? completionPct = null,
        string? orderId = null,
        string? topReason = null,
        IReadOnlyList<StopReasonAggregate>? stopReasons = null)
    {
        return new MeasurementReceived
        {
            ChannelId = channelId,
            WindowStart = DateTimeOffset.UtcNow.AddHours(-1),
            WindowEnd = DateTimeOffset.UtcNow,
            ProducedUnits = 100,
            RuntimeSeconds = 3600,
            TotalDowntimeMinutes = totalDowntimeMinutes,
            TopReason = topReason,
            OrderId = orderId,
            OrderTarget = orderId is null ? null : 200,
            CompletionPct = completionPct,
            StopReasons = stopReasons ?? Array.Empty<StopReasonAggregate>()
        };
    }

    // ------------------------------------------------------------------
    // Krav 1 – under tærsklerne udløses ingen kritikalitetsregler.
    // ------------------------------------------------------------------
    [Fact(DisplayName = "Måling under alle tærskler klassificeres som ikke-kritisk")]
    public void Evaluate_BelowAllThresholds_ReturnsNotCritical()
    {
        // Arrange
        var sut = CreateSut();
        var measurement = BuildMeasurement(
            totalDowntimeMinutes: 5,
            completionPct: 80.0,
            orderId: "ORDER-OK",
            stopReasons: new[]
            {
                new StopReasonAggregate
                {
                    Reason = "Mindre justering",
                    DurationMinutes = 2,
                    Occurrences = 1
                }
            });

        // Act
        var result = sut.Evaluate(measurement);

        // Assert
        result.IsCritical.Should().BeFalse();
        result.PrimaryRule.Should().BeNull();
        result.TriggeredRules.Should().BeEmpty();
        result.ObservedCriticalReasons.Should().BeEmpty();
        result.Description.Should().Be("Ingen kritiske regler udløst.");
    }

    // ------------------------------------------------------------------
    // Krav 2 – over tærskel udløser DowntimeExceedsThreshold.
    // ------------------------------------------------------------------
    [Fact(DisplayName = "Nedetid over tærsklen udløser DowntimeExceedsThreshold")]
    public void Evaluate_DowntimeExceedsThreshold_TriggersDowntimeRule()
    {
        // Arrange
        var sut = CreateSut();
        var measurement = BuildMeasurement(
            totalDowntimeMinutes: 45,
            completionPct: 90.0,
            orderId: "ORDER-LATE",
            topReason: "Mekanisk fejl");

        // Act
        var result = sut.Evaluate(measurement);

        // Assert
        result.IsCritical.Should().BeTrue();
        result.PrimaryRule.Should().Be("DowntimeExceedsThreshold");
        result.TriggeredRules.Should().ContainSingle()
            .Which.Should().Be("DowntimeExceedsThreshold");
        result.Description.Should().Contain("Samlet nedetid: 45 min.");
        result.Description.Should().Contain("Hyppigste årsag: Mekanisk fejl.");
    }

    [Fact(DisplayName = "Nedetid præcist på tærsklen udløser DowntimeExceedsThreshold (>=)")]
    public void Evaluate_DowntimeAtExactThreshold_TriggersDowntimeRule()
    {
        // Arrange – grænsetilfælde: regelmotoren bruger >= og skal derfor udløse ved 30.
        var sut = CreateSut();
        var measurement = BuildMeasurement(totalDowntimeMinutes: 30);

        // Act
        var result = sut.Evaluate(measurement);

        // Assert
        result.IsCritical.Should().BeTrue();
        result.TriggeredRules.Should().Contain("DowntimeExceedsThreshold");
    }

    // ------------------------------------------------------------------
    // Kritisk-keyword reglen.
    // ------------------------------------------------------------------
    [Theory(DisplayName = "Stopårsag der matcher kritisk-keyword udløser CriticalReasonObserved")]
    [InlineData("El-fejl i hovedtavle")]
    [InlineData("Pludseligt papirbrud i bane 2")]
    [InlineData("NØDSTOP aktiveret af operatør")]
    public void Evaluate_KnownCriticalKeyword_TriggersReasonRule(string reason)
    {
        // Arrange
        var sut = CreateSut();
        var measurement = BuildMeasurement(
            totalDowntimeMinutes: 5,
            completionPct: 90.0,
            orderId: "ORDER-K",
            stopReasons: new[]
            {
                new StopReasonAggregate
                {
                    Reason = reason,
                    DurationMinutes = 4,
                    Occurrences = 1
                }
            });

        // Act
        var result = sut.Evaluate(measurement);

        // Assert
        result.IsCritical.Should().BeTrue();
        result.TriggeredRules.Should().Contain("CriticalReasonObserved");
        result.ObservedCriticalReasons.Should().ContainSingle()
            .Which.Should().Be(reason);
    }

    // ------------------------------------------------------------------
    // Færdiggørelsesgrad-reglen og dens kortslutning på manglende OrderId.
    // ------------------------------------------------------------------
    [Fact(DisplayName = "Lav færdiggørelse for kendt ordre udløser OrderCompletionBelowThreshold")]
    public void Evaluate_LowCompletionPctWithOrderId_TriggersCompletionRule()
    {
        var sut = CreateSut();
        var measurement = BuildMeasurement(
            totalDowntimeMinutes: 5,
            completionPct: 30.0,
            orderId: "ORDER-LOW");

        var result = sut.Evaluate(measurement);

        result.IsCritical.Should().BeTrue();
        result.TriggeredRules.Should().Contain("OrderCompletionBelowThreshold");
    }

    [Fact(DisplayName = "Lav færdiggørelse uden OrderId udløser ikke OrderCompletionBelowThreshold")]
    public void Evaluate_LowCompletionWithoutOrderId_DoesNotTriggerCompletionRule()
    {
        var sut = CreateSut();
        var measurement = BuildMeasurement(
            totalDowntimeMinutes: 5,
            completionPct: 10.0,
            orderId: null);

        var result = sut.Evaluate(measurement);

        result.IsCritical.Should().BeFalse();
        result.TriggeredRules.Should().NotContain("OrderCompletionBelowThreshold");
    }

    // ------------------------------------------------------------------
    // Krav 3 – håndtering af malformerede / mangelfulde inputs.
    // ------------------------------------------------------------------
    [Fact(DisplayName = "Null-måling kaster ArgumentNullException uden at korrumpere regelmotoren")]
    public void Evaluate_NullMeasurement_ThrowsArgumentNullException()
    {
        var sut = CreateSut();

        var act = () => sut.Evaluate(null!);

        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("measurement");
    }

    [Theory(DisplayName = "Tom eller whitespace-stopårsag klassificeres ikke som kritisk")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void IsCriticalReason_MalformedReason_ReturnsFalse(string? reason)
    {
        var sut = CreateSut();

        sut.IsCriticalReason(reason).Should().BeFalse();
    }

    [Fact(DisplayName = "Måling med whitespace-stopårsag og ingen øvrige overtrædelser klassificeres som ikke-kritisk")]
    public void Evaluate_StopReasonWithWhitespace_DoesNotTriggerReasonRule()
    {
        // Arrange – defensivt edge-case: en aggregeret stopårsag kan opstå
        // med tom/whitespace 'Reason', f.eks. ved manglende operatør-input
        // i feed'et. Regelmotoren skal ikke fejle og skal ikke fejlagtigt
        // markere hændelsen kritisk.
        var sut = CreateSut();
        var measurement = BuildMeasurement(
            totalDowntimeMinutes: 5,
            completionPct: 80.0,
            orderId: "ORDER-WS",
            stopReasons: new[]
            {
                new StopReasonAggregate
                {
                    Reason = "   ",
                    DurationMinutes = 1,
                    Occurrences = 1
                }
            });

        var result = sut.Evaluate(measurement);

        result.IsCritical.Should().BeFalse();
        result.ObservedCriticalReasons.Should().BeEmpty();
    }

    [Fact(DisplayName = "Tom stopårsags-liste håndteres som ikke-kritisk uden exception")]
    public void Evaluate_EmptyStopReasonsList_DoesNotThrow()
    {
        var sut = CreateSut();
        var measurement = BuildMeasurement(
            totalDowntimeMinutes: 0,
            completionPct: 100.0,
            orderId: "ORDER-EMPTY",
            stopReasons: Array.Empty<StopReasonAggregate>());

        var act = () => sut.Evaluate(measurement);
        var result = act.Should().NotThrow().Subject;

        result.IsCritical.Should().BeFalse();
    }

    // ------------------------------------------------------------------
    // Sammensatte sager – flere regler udløst samtidig.
    // ------------------------------------------------------------------
    [Fact(DisplayName = "Sammensat overtrædelse udløser flere regler og PrimaryRule er den første")]
    public void Evaluate_MultipleViolations_AggregatesAllTriggeredRules()
    {
        var sut = CreateSut();
        var measurement = BuildMeasurement(
            totalDowntimeMinutes: 60,
            completionPct: 25.0,
            orderId: "ORDER-MULTI",
            stopReasons: new[]
            {
                new StopReasonAggregate
                {
                    Reason = "Banestyringsfejl - module 4",
                    DurationMinutes = 30,
                    Occurrences = 2
                }
            });

        var result = sut.Evaluate(measurement);

        result.IsCritical.Should().BeTrue();
        result.TriggeredRules.Should().BeEquivalentTo(new[]
        {
            "DowntimeExceedsThreshold",
            "CriticalReasonObserved",
            "OrderCompletionBelowThreshold"
        });
        result.PrimaryRule.Should().Be("DowntimeExceedsThreshold");
        result.ObservedCriticalReasons.Should().ContainSingle()
            .Which.Should().Contain("Banestyringsfejl");
    }
}

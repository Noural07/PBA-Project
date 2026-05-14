using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Options;
using Pba.Shared.Contracts.V1;

namespace AnalyzerService.Rules;

/// <summary>
/// Domæne-regelmotor der evaluerer en indkommen <see cref="MeasurementReceived"/>
/// mod tre uafhængige kritikalitetsregler:
/// <list type="number">
///   <item><description><b>DowntimeExceedsThreshold</b> – samlet nedetid over tærsklen.</description></item>
///   <item><description><b>CriticalReasonObserved</b> – mindst én aggregeret stopårsag matcher en kritisk-keyword.</description></item>
///   <item><description><b>OrderCompletionBelowThreshold</b> – kendt ordres færdiggørelsesgrad er under tærsklen.</description></item>
/// </list>
/// Klassen er ren og uden infrastruktur-afhængigheder, hvilket muliggør
/// enheds-test i Phase 5.
/// </summary>
public sealed class CriticalRuleEvaluator
{
    private readonly CriticalRuleOptions _options;

    public CriticalRuleEvaluator(IOptions<CriticalRuleOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
    }

    public CriticalEvaluationResult Evaluate(MeasurementReceived measurement)
    {
        ArgumentNullException.ThrowIfNull(measurement);

        var triggered = new List<string>();
        var observedCriticalReasons = new List<string>();

        if (measurement.TotalDowntimeMinutes >= _options.TotalDowntimeThresholdMinutes)
        {
            triggered.Add("DowntimeExceedsThreshold");
        }

        foreach (var aggregate in measurement.StopReasons)
        {
            if (IsCriticalReason(aggregate.Reason))
            {
                observedCriticalReasons.Add(aggregate.Reason);
            }
        }

        if (observedCriticalReasons.Count > 0)
        {
            triggered.Add("CriticalReasonObserved");
        }

        if (measurement.CompletionPct is { } completion
            && completion < _options.MinimumCompletionPct
            && !string.IsNullOrEmpty(measurement.OrderId))
        {
            triggered.Add("OrderCompletionBelowThreshold");
        }

        var isCritical = triggered.Count > 0;
        var primary = triggered.FirstOrDefault();
        var description = isCritical
            ? BuildDescription(measurement, triggered, observedCriticalReasons)
            : "Ingen kritiske regler udløst.";

        return new CriticalEvaluationResult(
            isCritical,
            primary,
            triggered,
            observedCriticalReasons,
            description);
    }

    /// <summary>
    /// Returnerer hvilke aggregerede stopårsager der matcher kritisk-listen.
    /// Anvendes både af regelmotoren selv og af konsumenten, der gemmer
    /// flag på de persistente <c>StopEvent</c>-rækker.
    /// </summary>
    public bool IsCriticalReason(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return false;
        }

        foreach (var keyword in _options.CriticalReasonKeywords)
        {
            if (reason.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string BuildDescription(
        MeasurementReceived measurement,
        List<string> triggered,
        List<string> observedReasons)
    {
        var parts = new List<string>
        {
            $"Kritisk hændelse for kanal {measurement.ChannelId}.",
            $"Samlet nedetid: {measurement.TotalDowntimeMinutes} min."
        };

        if (!string.IsNullOrEmpty(measurement.TopReason))
        {
            parts.Add($"Hyppigste årsag: {measurement.TopReason}.");
        }

        if (measurement.CompletionPct is { } completion)
        {
            parts.Add($"Færdiggørelsesgrad: {completion:F1}%.");
        }

        if (observedReasons.Count > 0)
        {
            parts.Add($"Kritiske årsager observeret: {string.Join(", ", observedReasons)}.");
        }

        parts.Add($"Udløste regler: {string.Join(", ", triggered)}.");
        return string.Join(" ", parts);
    }
}

/// <summary>
/// Resultatet af en kritikalitetsevaluering. Holdes som <c>record</c> for
/// at understrege at det er en immutabel domæneværdi, der kan logges
/// trygt og passes videre uden bekymring for muteret tilstand.
/// </summary>
public sealed record CriticalEvaluationResult(
    bool IsCritical,
    string? PrimaryRule,
    IReadOnlyList<string> TriggeredRules,
    IReadOnlyList<string> ObservedCriticalReasons,
    string Description);

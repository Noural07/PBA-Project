using IngestionService.Trendlog;

namespace IngestionService.Pipeline;

/// <summary>
/// Abstraktion for normaliseringspipelinen. Holdes adskilt fra den konkrete
/// implementering for at muliggøre enheds-test (Phase 5) uden at involvere
/// hverken HTTP-stack eller MassTransit-bus.
/// </summary>
public interface IMeasurementNormalizer
{
    NormalizationResult Normalize(TrendlogBatchPayload payload);
}

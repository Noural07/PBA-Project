namespace AiService.Gemini;

/// <summary>
/// Renset klassifikationsresultat returneret af <see cref="IGeminiClassifier"/>.
/// </summary>
public sealed record GeminiClassificationResult(
    string Category,
    string Subcategory,
    string StandardizedReason,
    string Severity,
    string RecommendedAction,
    double Confidence,
    long LatencyMs,
    bool IsFallback);

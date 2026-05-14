namespace AiService.Gemini;

/// <summary>
/// Det rensede domæne-resultat af et kald til Gemini. Klassen er bevidst
/// minimal og afkoblet fra Gemini's HTTP-respons-struktur, så klassifikationen
/// kan udskiftes (eksempelvis med en lokal model) uden at det påvirker
/// hverken konsumenten eller den udgående kontrakt.
/// </summary>
/// <param name="Category">
/// Trendlog grov-kategori (<c>Fault</c>, <c>Maintenance</c>, <c>Break</c>,
/// <c>Other</c>) eller <c>Unclassified</c> ved fallback.
/// </param>
/// <param name="Subcategory">
/// Dansk, specifik underkategori udledt af Gemini (maks. 64 tegn).
/// </param>
/// <param name="StandardizedReason">
/// Kort, normaliseret beskrivelse på dansk (maks. 80 tegn).
/// </param>
/// <param name="Severity">
/// Estimeret alvorlighedsgrad: <c>Low</c>, <c>Medium</c>, <c>High</c> eller
/// <c>Critical</c>.
/// </param>
/// <param name="RecommendedAction">
/// Kort, dansksproget anbefaling til operatøren (maks. 120 tegn).
/// </param>
/// <param name="Confidence">Modellens tillid i intervallet [0;1].</param>
/// <param name="LatencyMs">
/// Det reelle netværks-/inferens-budget for kaldet i millisekunder.
/// <c>0</c> ved fallback uden HTTP-kald.
/// </param>
/// <param name="IsFallback">
/// <c>true</c> hvis resultatet er udledt deterministisk fordi modellen ikke
/// var tilgængelig eller returnerede et ugyldigt svar.
/// </param>
public sealed record GeminiClassificationResult(
    string Category,
    string Subcategory,
    string StandardizedReason,
    string Severity,
    string RecommendedAction,
    double Confidence,
    long LatencyMs,
    bool IsFallback);

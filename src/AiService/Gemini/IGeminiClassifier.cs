using System.Threading;
using System.Threading.Tasks;

namespace AiService.Gemini;

/// <summary>
/// Kontrakt for klassifikation af en operatørs fri-tekst stopårsag.
/// </summary>
public interface IGeminiClassifier
{
    /// <summary>
    /// Klassificerer et stop ud fra fri-tekst, Trendlog-kategori og varighed.
    /// </summary>
    /// <param name="reason">Operatørens fri-tekst (kan være tom).</param>
    /// <param name="trendlogCategory">Trendlogs grov-kategori — vejledende kontekst, ikke bindende. <c>null</c> hvis ikke tilgængelig.</param>
    /// <param name="durationMinutes">Stoppets varighed i minutter.</param>
    /// <param name="cancellationToken">Annulleringstoken.</param>
    Task<GeminiClassificationResult> ClassifyAsync(
        string reason,
        string? trendlogCategory,
        int durationMinutes,
        CancellationToken cancellationToken);
}

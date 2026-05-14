using System.Threading;
using System.Threading.Tasks;

namespace AiService.Gemini;

/// <summary>
/// Domænekontrakt for klassifikation af en operatørs fri-tekst stopårsag.
/// Abstraktionen er introduceret for at lade konsumenten i RabbitMQ-pipelinen
/// være afhængig af en stabil grænseflade snarere end af Gemini-klientens
/// konkrete implementering. Dette letter både enheds-test og en fremtidig
/// udskiftning af AI-leverandøren.
/// </summary>
public interface IGeminiClassifier
{
    /// <summary>
    /// Klassificerer et operatør-registreret stop ud fra fri-tekst, en
    /// eventuelt allerede ekstraheret Trendlog-kategori og varigheden.
    /// </summary>
    /// <param name="reason">Operatørens fri-tekst (kan være tom).</param>
    /// <param name="trendlogCategory">
    /// Den grov-kategori som Trendlog selv har ledsaget kommentaren med
    /// (typisk <c>Fault</c>, <c>Maintenance</c> eller <c>Break</c>). Anvendes
    /// som svag kontekst til Gemini, men er ikke bindende — modellen kan
    /// vælge en anden kategori hvis fri-teksten klart peger derhen.
    /// Værdien <c>null</c> indikerer at Trendlog ikke leverede en kategori.
    /// </param>
    /// <param name="durationMinutes">Stoppets varighed i minutter.</param>
    /// <param name="cancellationToken">Annulleringstoken.</param>
    Task<GeminiClassificationResult> ClassifyAsync(
        string reason,
        string? trendlogCategory,
        int durationMinutes,
        CancellationToken cancellationToken);
}

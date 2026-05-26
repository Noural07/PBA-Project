using System;

namespace AiService.Gemini;

/// <summary>
/// Konfigurationsindstillinger for Gemini-integrationen (API-nøgle hentes fra miljøvariabel, ikke herfra).
/// </summary>
public sealed class GeminiOptions
{
    public const string SectionName = "Gemini";

    /// <summary>
    /// Base-URL for Generative Language API'et
    /// (default <c>https://generativelanguage.googleapis.com</c>).
    /// </summary>
    public string BaseUrl { get; set; } = "https://generativelanguage.googleapis.com";

    /// <summary>
    /// Model-id, eksempelvis <c>gemini-2.0-flash</c>. Holdes konfigurerbart, så
    /// klassifikationsstrategien kan justeres uden kodeændringer.
    /// </summary>
    public string Model { get; set; } = "gemini-2.0-flash";

    /// <summary>HTTP-timeout i sekunder for et enkelt kald til Gemini.</summary>
    public int TimeoutSeconds { get; set; } = 15;

    /// <summary>
    /// Modellens "temperature" – holdes lav for deterministiske,
    /// klassifikatoriske svar i et fast vokabular.
    /// </summary>
    public double Temperature { get; set; } = 0.1;

    /// <summary>Maksimalt antal output-tokens pr. kald.</summary>
    public int MaxOutputTokens { get; set; } = 256;
}

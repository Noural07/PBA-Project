using System.Collections.Generic;

namespace AnalyzerService.Rules;

/// <summary>
/// Konfiguration for kritikalitetsreglerne — kan overskrives via <c>Analyzer:Critical</c> eller miljøvariable.
/// </summary>
public sealed class CriticalRuleOptions
{
    public const string SectionName = "Analyzer:Critical";

    /// <summary>
    /// Samlet nedetid i minutter der udløser <c>DowntimeExceedsThreshold</c>.
    /// </summary>
    public int TotalDowntimeThresholdMinutes { get; set; } = 30;

    /// <summary>
    /// Tærskel i procent for færdiggørelsesgrad. Hvis ordrekonteksten er kendt
    /// og graden ligger under denne værdi, udløses reglen
    /// <c>OrderCompletionBelowThreshold</c>.
    /// </summary>
    public double MinimumCompletionPct { get; set; } = 50.0;

    /// <summary>
    /// Liste af stopårsager der altid betragtes som kritiske, uanset varighed.
    /// Match foretages ved case-insensitiv sub-string-sammenligning.
    /// </summary>
    public List<string> CriticalReasonKeywords { get; set; } =
    [
        "Banestyringsfejl",
        "Papirbrud",
        "Nødstop",
        "El-fejl"
    ];
}

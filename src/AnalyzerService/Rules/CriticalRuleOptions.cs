using System.Collections.Generic;

namespace AnalyzerService.Rules;

/// <summary>
/// Konfiguration for kritikalitetsreglerne i <c>AnalyzerService</c>.
/// Værdierne kan overskrives via konfigurations-sektionen
/// <c>Analyzer:Critical</c> eller via tilsvarende
/// <c>Analyzer__Critical__*</c>-miljøvariable, så drift kan justere
/// tærsklerne uden re-build.
/// </summary>
public sealed class CriticalRuleOptions
{
    public const string SectionName = "Analyzer:Critical";

    /// <summary>
    /// Tærskel i minutter for samlet nedetid i et tidsvindue. Overskridelse
    /// udløser reglen <c>DowntimeExceedsThreshold</c>.
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

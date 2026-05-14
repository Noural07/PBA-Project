using System.ComponentModel.DataAnnotations;

namespace IngestionService.Trendlog;

/// <summary>
/// Konfigurations-binding for Trendlog-integrationen.
/// <para>
/// <see cref="BaseUrl"/> indlæses fra <c>appsettings.json</c> sektionen
/// <c>Trendlog</c> (kan overrides via miljøvariablen <c>Trendlog__BaseUrl</c>).
/// <see cref="ApiKey"/> indlæses udelukkende fra miljøvariablen
/// <c>TRENDLOG_API_KEY</c> for at sikre at hemmeligheder aldrig committes til
/// kildekontrol, jf. projektets sikkerhedsdirektiver.
/// </para>
/// </summary>
public sealed class TrendlogOptions
{
    public const string SectionName = "Trendlog";
    public const string ApiKeyEnvironmentVariable = "TRENDLOG_API_KEY";

    /// <summary>
    /// Basis-URL for Trendlog-API'et, fx <c>https://api.trendlog.io</c>.
    /// </summary>
    [Required(AllowEmptyStrings = false)]
    [Url]
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// Bearer-token til Trendlog. Sættes ved opstart fra
    /// <see cref="ApiKeyEnvironmentVariable"/> og er <c>null</c> hvis miljøet
    /// ikke har defineret variablen — i så fald fejler validation.
    /// </summary>
    [Required(AllowEmptyStrings = false)]
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Maksimal default tilbageblik i minutter for probe-endpoints.
    /// Anvendes når kalderen ikke selv specificerer tidsvindue.
    /// </summary>
    [Range(1, 1440)]
    public int DefaultLookbackMinutes { get; set; } = 60;

    /// <summary>
    /// HTTP-timeout i sekunder for kald mod Trendlog. Holdes lavt for at undgå
    /// langvarige hængende anmodninger ved netværksfejl. Anvendes som
    /// fallback hvis resilience-handleren ikke afbryder først.
    /// </summary>
    [Range(1, 120)]
    public int HttpTimeoutSeconds { get; set; } = 60;

    /// <summary>
    /// Indre per-attempt-timeout i sekunder under Polly's
    /// <c>StandardResilienceHandler</c>. Et enkelt forsøg afbrydes når denne
    /// tærskel overskrides, så retry-strategien kan tage over.
    /// </summary>
    [Range(1, 60)]
    public int AttemptTimeoutSeconds { get; set; } = 10;

    /// <summary>
    /// Maksimalt antal retries (eksklusiv det indledende forsøg). Kombineret
    /// med eksponentiel backoff svarer 3 retries til ca. 1 + 2 + 4 sekunder
    /// mellem forsøg, hvilket holder kaldet inden for det totale timeout-budget.
    /// </summary>
    [Range(0, 10)]
    public int RetryMaxAttempts { get; set; } = 3;

    /// <summary>
    /// Antal observerede fejl indenfor <see cref="CircuitBreakerSamplingDurationSeconds"/>
    /// før circuit-breakeren bryder strømmen. Sat til 5 jf. fase B-kravene.
    /// </summary>
    [Range(2, 50)]
    public int CircuitBreakerMinimumThroughput { get; set; } = 5;

    /// <summary>
    /// Sampling-vinduet i sekunder, hvor circuit-breakeren akkumulerer fejl.
    /// Skal være større end <see cref="AttemptTimeoutSeconds"/> for at undgå
    /// at et enkelt timeout-event lukker kredsløbet.
    /// </summary>
    [Range(10, 600)]
    public int CircuitBreakerSamplingDurationSeconds { get; set; } = 30;

    /// <summary>
    /// Andel af fejlede kald i samplingsvinduet, der udløser circuit-breakeren.
    /// 0,5 = 50 %. Holdes lav for at fange degraderede opstrøms-tjenester
    /// hurtigt under demonstration.
    /// </summary>
    [Range(0.1, 1.0)]
    public double CircuitBreakerFailureRatio { get; set; } = 0.5;
}

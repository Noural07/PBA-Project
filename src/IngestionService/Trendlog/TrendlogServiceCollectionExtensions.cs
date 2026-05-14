using System;
using System.Threading;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;
using Polly;

namespace IngestionService.Trendlog;

/// <summary>
/// Registrerer Trendlog-integrationen (options, typed HttpClient, resilience-pipeline
/// og response-mapper) på <see cref="IServiceCollection"/>.
/// </summary>
public static class TrendlogServiceCollectionExtensions
{
    public static IServiceCollection AddTrendlogIntegration(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services
            .AddOptions<TrendlogOptions>()
            .Bind(configuration.GetSection(TrendlogOptions.SectionName))
            .Configure(options =>
            {
                // API-nøgle hentes ALDRIG fra appsettings — udelukkende fra miljøvariabel.
                var apiKey = Environment.GetEnvironmentVariable(TrendlogOptions.ApiKeyEnvironmentVariable);
                if (!string.IsNullOrWhiteSpace(apiKey))
                {
                    options.ApiKey = apiKey.Trim();
                }
            })
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // Diagnostisk handler skal være registreret før den hægtes på klienten.
        services.AddTransient<TrendlogLoggingHandler>();

        // Mapper er stateless og indeholder kun en logger-reference.
        services.AddSingleton<ITrendlogResponseMapper, TrendlogResponseMapper>();

        services.AddHttpClient<ITrendlogClient, TrendlogClient>((sp, client) =>
        {
            var options = sp.GetRequiredService<IOptions<TrendlogOptions>>().Value;
            client.BaseAddress = new Uri(options.BaseUrl, UriKind.Absolute);

            // Timeout-styring delegeres bevidst til Polly's
            // <c>StandardResilienceHandler</c>. Hvis HttpClient selv havde et
            // kortere timeout, ville det skære retry-forsøg af, og
            // timeout-håndteringen ville være duplikeret to steder.
            client.Timeout = Timeout.InfiniteTimeSpan;
            client.DefaultRequestHeaders.Accept.ParseAdd("application/json");

            // User-Agent skal matche en kendt klient-signatur. En custom UA
            // (som "PBA-IngestionService/1.0") får Trendlogs API-gateway til at
            // returnere 406 Not Acceptable med en HTML-fejlside i stedet for JSON.
            // En curl-lignende værdi accepteres uden problemer.
            client.DefaultRequestHeaders.UserAgent.ParseAdd("curl/8.4.0");
        })
        .AddHttpMessageHandler<TrendlogLoggingHandler>()
        .AddStandardResilienceHandler((handlerOptions) =>
        {
            // Bemærk: handler-options gælder for det enkelte HttpClient-kald —
            // konfigurationen læses derfor ikke pr. request. Tærsklerne hentes
            // fra IConfiguration ved registreringstidspunktet for at give
            // ops-teamet mulighed for at justere via Trendlog__-præfiks
            // miljøvariabler uden code-deploy.
            ConfigureResilience(handlerOptions, configuration);
        });

        return services;
    }

    private static void ConfigureResilience(
        HttpStandardResilienceOptions handler,
        IConfiguration configuration)
    {
        var section = configuration.GetSection(TrendlogOptions.SectionName);
        var bound = section.Get<TrendlogOptions>() ?? new TrendlogOptions();

        var attemptTimeout = TimeSpan.FromSeconds(bound.AttemptTimeoutSeconds);
        var samplingDuration = TimeSpan.FromSeconds(bound.CircuitBreakerSamplingDurationSeconds);

        // Indre per-forsøg-timeout. Et enkelt forsøg må ikke hænge i mere
        // end 10 sekunder før Polly kasserer det og lader retry-policyen reagere.
        handler.AttemptTimeout.Timeout = attemptTimeout;

        // Eksponentiel backoff med jitter for at undgå "thundering herd" mod
        // Trendlog ved samtidige genforsøg fra flere instanser. Jitter er
        // default-on i Polly v8.
        handler.Retry.MaxRetryAttempts = bound.RetryMaxAttempts;
        handler.Retry.BackoffType = DelayBackoffType.Exponential;
        handler.Retry.UseJitter = true;
        handler.Retry.Delay = TimeSpan.FromSeconds(1);

        // Circuit-breakeren bryder strømmen, hvis 5 fejl observeres inden for
        // 30 sekunder. Sampling-vinduet skal mindst dække ét par retries +
        // backoff for at undgå falske positiver.
        handler.CircuitBreaker.MinimumThroughput = bound.CircuitBreakerMinimumThroughput;
        handler.CircuitBreaker.SamplingDuration = samplingDuration;
        handler.CircuitBreaker.FailureRatio = bound.CircuitBreakerFailureRatio;

        // Total request-budget = (attempt-timeout × (retries + 1)) + max backoff,
        // med en sikkerhedsmargin. Dette sikrer at TotalRequestTimeout altid er
        // strengt større end AttemptTimeout, hvilket Polly's validator håndhæver.
        var totalBudgetSeconds = Math.Max(
            samplingDuration.TotalSeconds + 5,
            (bound.RetryMaxAttempts + 1) * bound.AttemptTimeoutSeconds + 10);
        handler.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(totalBudgetSeconds);
    }
}

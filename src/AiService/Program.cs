using System;
using System.Net.Http;
using AiService.Consumers;
using AiService.Gemini;
using HealthChecks.UI.Client;
using MassTransit;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;
using Pba.Shared.HealthChecks;
using Pba.Shared.Messaging;
using Pba.Shared.Observability;
using Polly;
using Serilog;

const string ServiceName = "ai-service";

SerilogBootstrapper.ConfigureBootstrapLogger(ServiceName);

try
{
    Log.Information("Bootstrapping {Service}", ServiceName);

    var builder = WebApplication.CreateBuilder(args);
    builder.UseStandardSerilog(ServiceName);

    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen(options =>
    {
        options.SwaggerDoc("v1", new Microsoft.OpenApi.Models.OpenApiInfo
        {
            Title = $"{ServiceName} API",
            Version = "v1",
            Description = "AI classification service for free-text operator stop reasons via Gemini (Phase 4)."
        });
    });

    // Database-per-Service: AiService er en stateless event-konsument der
    // kun forholder sig til Gemini-HTTP og RabbitMQ — ingen DbContext, ingen
    // Postgres-afhængighed i readiness-checket.
    builder.Services.AddPlatformHealthChecks(builder.Configuration, requirePostgres: false);

    // ----------------------------------------------------------------------
    // Phase 4 – Gemini-integration.
    //
    // Sikkerhed: API-nøglen hentes UDELUKKENDE fra miljøvariablen
    // GEMINI_API_KEY i selve klienten. Tilstedeværelsen logges som structured
    // warning hvis nøglen mangler – selve værdien logges aldrig.
    // ----------------------------------------------------------------------
    builder.Services.AddOptions<GeminiOptions>()
        .Bind(builder.Configuration.GetSection(GeminiOptions.SectionName));

    var geminiApiKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY");
    if (string.IsNullOrWhiteSpace(geminiApiKey))
    {
        Log.Warning(
            "{Service} started without GEMINI_API_KEY – classification will run in fallback mode.",
            ServiceName);
    }

    var geminiBaseUrl = builder.Configuration["Gemini:BaseUrl"]
        ?? "https://generativelanguage.googleapis.com";
    var geminiTimeoutSeconds = int.TryParse(
        builder.Configuration["Gemini:TimeoutSeconds"], out var parsedTimeout)
        ? parsedTimeout
        : 15;

    // ----------------------------------------------------------------------
    // Phase 5 – Resilience.
    //
    // Polly anvendes via Microsoft.Extensions.Http.Resilience til at indkapsle
    // HttpClient'en mod Gemini i en pipeline med (1) en eksponentiel
    // Retry-strategi med jitter (3 forsøg) og (2) en Circuit Breaker, der
    // åbner ved 50% fejlrate over et 30-sekunders sample-vindue. Strategien
    // sikrer, at ai-service overlever transiente Gemini-udfald uden at
    // crashe og uden at overbelaste et i forvejen ramt eksternt API.
    //
    // Hver retry- og breaker-tilstandsovergang logges som Serilog-event,
    // hvilket gør hændelserne synlige i Loki/Grafana med felterne
    // "Gemini.Resilience" og letter den evidens-baserede dokumentation
    // af resilience-adfærden i bachelorrapporten.
    //
    // HttpClient.Timeout sættes bevidst højere end den indre timeout-budget,
    // så Polly's egne retries når at afvikle inden for klient-timeouten.
    // ----------------------------------------------------------------------
    builder.Services
        .AddHttpClient(GeminiClassifier.HttpClientName, client =>
        {
            client.BaseAddress = new Uri(
                geminiBaseUrl.EndsWith('/') ? geminiBaseUrl : geminiBaseUrl + "/");
            client.Timeout = TimeSpan.FromSeconds(geminiTimeoutSeconds * 4);
            client.DefaultRequestHeaders.Add("Accept", "application/json");
        })
        .AddResilienceHandler("gemini-resilience", (pipelineBuilder, context) =>
        {
            var resilienceLogger = context.ServiceProvider
                .GetRequiredService<ILoggerFactory>()
                .CreateLogger("Gemini.Resilience");

            // Indre timeout pr. forsøg – sikrer at et hængende kald ikke
            // bruger hele klient-timeout-budgettet.
            pipelineBuilder.AddTimeout(TimeSpan.FromSeconds(geminiTimeoutSeconds));

            // Retry-strategi: eksponentiel backoff med jitter for at undgå
            // synkroniserede genforsøg på tværs af konkurrerende konsumenter.
            pipelineBuilder.AddRetry(new HttpRetryStrategyOptions
            {
                Name = "Gemini-Retry",
                MaxRetryAttempts = 3,
                BackoffType = DelayBackoffType.Exponential,
                Delay = TimeSpan.FromSeconds(1),
                UseJitter = true,
                ShouldHandle = static args =>
                {
                    var transient = args.Outcome.Exception is HttpRequestException
                        || (args.Outcome.Result is HttpResponseMessage response
                            && IsTransientFailure(response));
                    return ValueTask.FromResult(transient);
                },
                OnRetry = args =>
                {
                    resilienceLogger.LogWarning(
                        "Gemini.Resilience retry attempt={Attempt}, delayMs={DelayMs}, status={Status}, exception={Exception}",
                        args.AttemptNumber + 1,
                        args.RetryDelay.TotalMilliseconds,
                        args.Outcome.Result?.StatusCode.ToString() ?? "n/a",
                        args.Outcome.Exception?.GetType().Name ?? "n/a");
                    return ValueTask.CompletedTask;
                }
            });

            // Circuit-Breaker: når fejlraten over 30 sekunder overskrider 50%
            // (med min. 5 kald), åbnes kredsløbet i 30 sekunder, hvorefter
            // der prøves halv-åben med ét kald.
            pipelineBuilder.AddCircuitBreaker(new HttpCircuitBreakerStrategyOptions
            {
                Name = "Gemini-CircuitBreaker",
                FailureRatio = 0.5,
                MinimumThroughput = 5,
                SamplingDuration = TimeSpan.FromSeconds(30),
                BreakDuration = TimeSpan.FromSeconds(30),
                ShouldHandle = static args =>
                {
                    var transient = args.Outcome.Exception is HttpRequestException
                        || (args.Outcome.Result is HttpResponseMessage response
                            && IsTransientFailure(response));
                    return ValueTask.FromResult(transient);
                },
                OnOpened = args =>
                {
                    resilienceLogger.LogError(
                        "Gemini.Resilience circuit-breaker OPENED for {DurationSeconds}s – yderligere kald afvises lokalt",
                        args.BreakDuration.TotalSeconds);
                    return ValueTask.CompletedTask;
                },
                OnClosed = args =>
                {
                    resilienceLogger.LogInformation(
                        "Gemini.Resilience circuit-breaker CLOSED – kald genoptages");
                    return ValueTask.CompletedTask;
                },
                OnHalfOpened = args =>
                {
                    resilienceLogger.LogInformation(
                        "Gemini.Resilience circuit-breaker HALF-OPEN – afprøver ét kald før fuld genåbning");
                    return ValueTask.CompletedTask;
                }
            });
        });

    builder.Services.AddTransient<IGeminiClassifier>(sp =>
    {
        var factory = sp.GetRequiredService<IHttpClientFactory>();
        var http = factory.CreateClient(GeminiClassifier.HttpClientName);
        return new GeminiClassifier(
            http,
            sp.GetRequiredService<IOptions<GeminiOptions>>(),
            sp.GetRequiredService<ILogger<GeminiClassifier>>());
    });

    // ----------------------------------------------------------------------
    // Phase 4 – Messaging (consumer på OperatorCommentRegistered).
    // ----------------------------------------------------------------------
    builder.Services.AddPlatformMassTransit(builder.Configuration, mt =>
    {
        mt.AddConsumer<OperatorCommentRegisteredConsumer>();
    });

    var app = builder.Build();

    app.UseSerilogRequestLogging();

    if (app.Environment.IsDevelopment())
    {
        app.UseSwagger();
        app.UseSwaggerUI();
    }

    app.MapHealthChecks("/health", new HealthCheckOptions
    {
        ResponseWriter = HealthEndpointWriter.WriteJsonResponse
    });

    app.MapHealthChecks("/health/live", new HealthCheckOptions
    {
        Predicate = registration => registration.Tags.Contains("live"),
        ResponseWriter = UIResponseWriter.WriteHealthCheckUIResponse
    });

    app.MapHealthChecks("/health/ready", new HealthCheckOptions
    {
        Predicate = registration => registration.Tags.Contains("ready"),
        ResponseWriter = UIResponseWriter.WriteHealthCheckUIResponse
    });

    app.MapGet("/", () => Results.Ok(new
    {
        service = ServiceName,
        status = "running",
        timestamp = DateTimeOffset.UtcNow
    }))
    .WithName("Root")
    .WithOpenApi();

    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "{Service} terminated unexpectedly during startup", ServiceName);
    throw;
}
finally
{
    Log.CloseAndFlush();
}

// ----------------------------------------------------------------------------
// Hjælpefunktion til at klassificere et HTTP-svar som transient. Anvendes både
// af Retry- og Circuit-Breaker-strategien i resilience-pipelinen ovenfor.
// 408 Request Timeout, 429 Too Many Requests og 5xx-status betragtes som
// midlertidige og udløser dermed retry/breaker. 4xx-fejl (bortset fra 408/429)
// regnes som permanente og forbigår resilience-pipelinen, så fejlkonfigureret
// API-nøgle eller ugyldig request-payload propageres uden unødige genforsøg.
// ----------------------------------------------------------------------------
static bool IsTransientFailure(HttpResponseMessage response)
{
    var status = (int)response.StatusCode;
    return status == 408
        || status == 429
        || status >= 500;
}

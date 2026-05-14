using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AiService.Gemini;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AiService.Tests;

/// <summary>
/// Verificerer <see cref="GeminiClassifier"/>'s deterministiske fallback-
/// adfærd uden at ramme det reelle Gemini-API. Den ikke-deterministiske
/// integration mod live Gemini holdes uden for unit-suiten og afprøves
/// alene manuelt eller via det dedikerede e2e-script i fase C-rapporten.
/// </summary>
public sealed class GeminiClassifierFallbackTests
{
    private static GeminiClassifier CreateSut()
    {
        var options = Options.Create(new GeminiOptions
        {
            BaseUrl = "https://generativelanguage.googleapis.com",
            Model = "gemini-2.5-flash",
            TimeoutSeconds = 5,
            Temperature = 0.1,
            MaxOutputTokens = 256
        });

        var http = new HttpClient
        {
            BaseAddress = new Uri("https://generativelanguage.googleapis.com/")
        };

        return new GeminiClassifier(http, options, NullLogger<GeminiClassifier>.Instance);
    }

    [Fact(DisplayName = "Tom fri-tekst giver Unclassified-fallback uden at kalde Gemini")]
    public async Task ClassifyAsync_EmptyReason_ReturnsUnclassifiedFallback()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        var result = await sut.ClassifyAsync(string.Empty, trendlogCategory: null,
            durationMinutes: 0, CancellationToken.None);

        // Assert
        result.IsFallback.Should().BeTrue();
        result.Category.Should().Be("Unclassified");
        result.Severity.Should().Be("Low");
        result.LatencyMs.Should().Be(0L);
        result.RecommendedAction.Should().Be("Kræver manuel gennemgang");
    }

    [Fact(DisplayName = "Manglende GEMINI_API_KEY giver FAKE-mode-fallback")]
    public async Task ClassifyAsync_MissingApiKey_ReturnsFakeModeFallback()
    {
        // Arrange — sikrer at miljøvariablen er tom for testen og rydder op bagefter.
        var existing = Environment.GetEnvironmentVariable("GEMINI_API_KEY");
        Environment.SetEnvironmentVariable("GEMINI_API_KEY", null);

        try
        {
            var sut = CreateSut();

            // Act
            var result = await sut.ClassifyAsync(
                "Pakkemaskine smed papir",
                trendlogCategory: "Fault",
                durationMinutes: 12,
                CancellationToken.None);

            // Assert — FAKE-mode genkendes på subcategory'en, så det aldrig kan
            // forveksles med et reelt model-output i log-traces.
            result.IsFallback.Should().BeTrue();
            result.Category.Should().Be("Unclassified");
            result.Subcategory.Should().Be("FAKE-mode (ingen API-nøgle)");
            result.LatencyMs.Should().Be(0L);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEMINI_API_KEY", existing);
        }
    }
}

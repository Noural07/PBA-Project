using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IngestionService.Trendlog;

/// <summary>
/// Typed <see cref="HttpClient"/>-implementation af <see cref="ITrendlogClient"/>.
/// Bemærk særligt:
/// <list type="bullet">
///   <item><description>Verbet er <c>POST</c> — ikke <c>GET</c>.</description></item>
///   <item><description>Bearer-token sættes pr. request via <c>Authorization</c>-headeren.</description></item>
///   <item><description><c>start</c> bindes til den nyeste dato og <c>end</c> til den ældste — modsat de fleste API-konventioner.</description></item>
///   <item><description>Datoer formateres som <c>dd-MM-yyyy</c> (dansk dag-først), ikke ISO 8601.</description></item>
///   <item><description>Bodyen er et JSON-array af feed-anmodninger (<see cref="TrendlogFeedRequest"/>).</description></item>
/// </list>
/// </summary>
public sealed class TrendlogClient : ITrendlogClient
{
    private const string TrendlogDateFormat = "dd-MM-yyyy";

    private static readonly JsonSerializerOptions BodyJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    private readonly HttpClient _httpClient;
    private readonly TrendlogOptions _options;
    private readonly ILogger<TrendlogClient> _logger;

    public TrendlogClient(
        HttpClient httpClient,
        IOptions<TrendlogOptions> options,
        ILogger<TrendlogClient> logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public Task<JsonElement> GetFeedsRawAsync(
        int channelId,
        int daysBack,
        IReadOnlyCollection<TrendlogFeedRequest> feedRequests,
        CancellationToken cancellationToken = default)
    {
        if (daysBack < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(daysBack),
                "daysBack skal være mindst 1.");
        }

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var oldest = today.AddDays(-daysBack);
        return GetFeedsRawAsync(channelId, today, oldest, feedRequests, cancellationToken);
    }

    public async Task<JsonElement> GetFeedsRawAsync(
        int channelId,
        DateOnly newest,
        DateOnly oldest,
        IReadOnlyCollection<TrendlogFeedRequest> feedRequests,
        CancellationToken cancellationToken = default)
    {
        if (channelId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(channelId),
                "Channel-id skal være positivt.");
        }

        if (newest < oldest)
        {
            throw new ArgumentException(
                "Trendlog-API'et kræver at 'start' (newest) ikke ligger før 'end' (oldest).",
                nameof(newest));
        }

        if (feedRequests is null || feedRequests.Count == 0)
        {
            throw new ArgumentException(
                "Mindst én feed-anmodning skal angives i bodyen.",
                nameof(feedRequests));
        }

        var channelSegment = channelId.ToString(CultureInfo.InvariantCulture);
        var startEncoded = Uri.EscapeDataString(
            newest.ToString(TrendlogDateFormat, CultureInfo.InvariantCulture));
        var endEncoded = Uri.EscapeDataString(
            oldest.ToString(TrendlogDateFormat, CultureInfo.InvariantCulture));
        var requestUri = $"/V1/channels/{channelSegment}/feeds?start={startEncoded}&end={endEncoded}";

        using var request = new HttpRequestMessage(HttpMethod.Post, requestUri);
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {_options.ApiKey}");
        request.Content = JsonContent.Create(feedRequests, options: BodyJsonOptions);

        // Trendlog afviser "application/json; charset=utf-8" med 406 Not Acceptable.
        // Headeren overskrives derfor til ren "application/json" — præcis som cURL og
        // Trendlogs eget Swagger UI sender den.
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        _logger.LogInformation(
            "Kalder Trendlog (POST) {RequestUri}. ChannelId={ChannelId} Start(newest)={Start} End(oldest)={End} FeedCount={Count}",
            requestUri, channelId, newest, oldest, feedRequests.Count);

        using var response = await _httpClient.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogWarning(
                "Trendlog svarede {StatusCode}. Body={Body}",
                (int)response.StatusCode, Truncate(errorBody, 500));
            throw new TrendlogApiException(
                (int)response.StatusCode,
                $"Trendlog returnerede HTTP {(int)response.StatusCode}: {response.ReasonPhrase}.",
                errorBody);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        // JsonElement skal kopieres ud af document-scope da document disposes her.
        return document.RootElement.Clone();
    }

    private static string Truncate(string value, int max)
        => string.IsNullOrEmpty(value) || value.Length <= max
            ? value ?? string.Empty
            : string.Concat(value.AsSpan(0, max), "...");
}

/// <summary>
/// Indikerer at et opstrøms-kald til Trendlog returnerede en fejl-status. Bærer
/// status-kode og rå body videre, så endpointet kan returnere en informativ
/// fejlbesked til Swagger uden at lække bearer-token eller intern stack trace.
/// </summary>
public sealed class TrendlogApiException : Exception
{
    public int StatusCode { get; }
    public string ResponseBody { get; }

    public TrendlogApiException(int statusCode, string message, string responseBody)
        : base(message)
    {
        StatusCode = statusCode;
        ResponseBody = responseBody ?? string.Empty;
    }
}

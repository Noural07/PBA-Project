using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace IngestionService.Trendlog;

/// <summary>
/// Diagnostisk <see cref="DelegatingHandler"/> der dumper outgoing
/// HTTP-requests og responses fra <see cref="TrendlogClient"/> til loggen.
/// Authorization-headeren maskeres, men dens længde rapporteres så det kan
/// verificeres at bearer-tokenet faktisk indlæses fra miljøet.
/// </summary>
public sealed class TrendlogLoggingHandler : DelegatingHandler
{
    private const int MaxBodyLogChars = 800;

    private readonly ILogger<TrendlogLoggingHandler> _logger;

    public TrendlogLoggingHandler(ILogger<TrendlogLoggingHandler> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        await LogRequestAsync(request, cancellationToken);

        var response = await base.SendAsync(request, cancellationToken);

        _logger.LogInformation(
            "TRENDLOG IN  << {StatusCode} {ReasonPhrase} ResponseContentType={ContentType}",
            (int)response.StatusCode,
            response.ReasonPhrase,
            response.Content.Headers.ContentType?.ToString() ?? "<none>");

        return response;
    }

    private async Task LogRequestAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var headers = new List<string>();

        foreach (var header in request.Headers)
        {
            if (string.Equals(header.Key, "Authorization", StringComparison.OrdinalIgnoreCase))
            {
                var totalLength = header.Value.Sum(v => v?.Length ?? 0);
                headers.Add($"Authorization: <masked, valueLength={totalLength}>");
            }
            else
            {
                headers.Add($"{header.Key}: {string.Join(", ", header.Value)}");
            }
        }

        if (request.Content is not null)
        {
            foreach (var header in request.Content.Headers)
            {
                headers.Add($"{header.Key}: {string.Join(", ", header.Value)}");
            }
        }

        string? bodyPreview = null;
        if (request.Content is not null)
        {
            try
            {
                var fullBody = await request.Content.ReadAsStringAsync(cancellationToken);
                bodyPreview = fullBody.Length > MaxBodyLogChars
                    ? string.Concat(fullBody.AsSpan(0, MaxBodyLogChars), "...")
                    : fullBody;
            }
            catch (Exception ex)
            {
                bodyPreview = $"<unable to read body: {ex.Message}>";
            }
        }

        _logger.LogInformation(
            "TRENDLOG OUT >> {Method} {Uri} | Headers=[{Headers}] | Body={Body}",
            request.Method,
            request.RequestUri,
            string.Join(" ; ", headers),
            bodyPreview ?? "<no body>");
    }
}

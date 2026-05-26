using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace IngestionService.Trendlog;

/// <summary>
/// Abstraktion over Trendlog-API'ets feeds-endpoint (POST, <c>dd-MM-yyyy</c>-datoformat,
/// <c>start</c> = nyeste dato, <c>end</c> = ældste).
/// </summary>

public interface ITrendlogClient
{
    /// <summary>
    /// Henter rå feed-respons for en kanal i et tidsvindue defineret af
    /// <paramref name="daysBack"/> dage tilbage fra i dag (UTC). Returnerer
    /// JSON som modtaget fra Trendlog uden semantisk fortolkning.
    /// </summary>
    /// <param name="channelId">Trendlog-kanalens numeriske id.</param>
    /// <param name="daysBack">Antal dage tilbage fra dags dato. Skal være >= 1.</param>
    /// <param name="feedRequests">Liste af feed-specifikationer der bygger POST-bodyen.</param>
    Task<JsonElement> GetFeedsRawAsync(
        int channelId,
        int daysBack,
        IReadOnlyCollection<TrendlogFeedRequest> feedRequests,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Som ovenstående, men med eksplicit defineret dato-interval. Husk:
    /// <paramref name="newest"/> svarer til <c>start</c>-parameteren og
    /// <paramref name="oldest"/> til <c>end</c>-parameteren i Trendlog-API'et.
    /// </summary>
    Task<JsonElement> GetFeedsRawAsync(
        int channelId,
        DateOnly newest,
        DateOnly oldest,
        IReadOnlyCollection<TrendlogFeedRequest> feedRequests,
        CancellationToken cancellationToken = default);
}

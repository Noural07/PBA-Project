using System;
using System.Text.Json;

namespace IngestionService.Trendlog;

/// <summary>
/// Mapper Trendlogs rå JSON-respons til <see cref="TrendlogBatchPayload"/>.
/// </summary>
public interface ITrendlogResponseMapper
{
    /// <summary>
    /// Mapper et Trendlog-respons til projektets DTO-form.
    /// </summary>
    /// <param name="raw">Det rå JSON-respons fra Trendlog.</param>
    /// <param name="channelId">Kanal-id fra request-context. Anvendes som fallback hvis svaret mangler eksplicit kanal-metadata.</param>
    /// <param name="windowStart">Logisk start på det aggregerede tidsvindue (UTC).</param>
    /// <param name="windowEnd">Logisk slut på det aggregerede tidsvindue (UTC).</param>
    TrendlogBatchPayload Map(
        JsonElement raw,
        int channelId,
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd);
}

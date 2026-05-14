using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using IngestionService.Trendlog;
using Microsoft.Extensions.Logging;
using Pba.Shared.Contracts.V1;

namespace IngestionService.Pipeline;

/// <summary>
/// Normaliserer et råt Trendlog-batch til de interne kontrakter
/// <see cref="MeasurementReceived"/> og <see cref="OperatorCommentRegistered"/>.
/// Implementerer trinene 1–4 i projektplanens afsnit 5.3:
/// <list type="number">
///   <item><description>Konvertering af rå feed-respons til interne DTO'er.</description></item>
///   <item><description>Gruppering af stop-events efter <c>reason</c> med summeret varighed.</description></item>
///   <item><description>Beregning af <c>totalDowntime</c> og <c>topReason</c>.</description></item>
///   <item><description>Ordrekobling og udregning af <c>completionPct</c>.</description></item>
/// </list>
/// </summary>
public sealed class MeasurementNormalizer : IMeasurementNormalizer
{
    private readonly ILogger<MeasurementNormalizer> _logger;

    public MeasurementNormalizer(ILogger<MeasurementNormalizer> logger)
    {
        _logger = logger;
    }

    public NormalizationResult Normalize(TrendlogBatchPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        var anomalies = new List<string>();
        var correlationId = Guid.NewGuid();

        // Trin 1: Aggregér produktion (XYZ01_cnt) og runtime (XYZ01_runtime).
        // Negative diff-værdier afvises som dataanomali (jf. plan §5.4).
        var (producedUnits, cntAnomalies) = SumDiffs(payload.Feeds.Count, "XYZ01_cnt");
        var (runtimeSec, runtimeAnomalies) = SumDiffs(payload.Feeds.Runtime, "XYZ01_runtime");
        anomalies.AddRange(cntAnomalies);
        anomalies.AddRange(runtimeAnomalies);

        // Trin 2: Gruppér stoptider pr. reason.
        var aggregates = payload.Feeds.Stoptime
            .Where(s => !string.IsNullOrWhiteSpace(s.Reason))
            .GroupBy(s => s.Reason.Trim())
            .Select(g => new StopReasonAggregate
            {
                Reason = g.Key,
                DurationMinutes = g.Sum(x => x.DurationMinutes),
                Occurrences = g.Count()
            })
            .OrderByDescending(a => a.DurationMinutes)
            .ToList();

        // Trin 3: totalDowntime + topReason.
        var totalDowntime = aggregates.Sum(a => a.DurationMinutes);
        var topReason = aggregates.FirstOrDefault()?.Reason;

        // Trin 4: Ordrekobling. Anvender seneste porder-entry i tidsvinduet.
        var orderEntry = payload.Feeds.Order
            .OrderByDescending(o => o.Timestamp)
            .FirstOrDefault();

        string? orderId = null;
        int? orderTarget = null;
        double? completionPct = null;

        if (orderEntry is not null && !string.IsNullOrWhiteSpace(orderEntry.OrderId))
        {
            orderId = orderEntry.OrderId;
            orderTarget = orderEntry.Kvantum > 0 ? orderEntry.Kvantum : null;
            if (orderTarget is { } target)
            {
                completionPct = Math.Round((double)producedUnits / target * 100.0, 2);
            }
        }
        else
        {
            anomalies.Add("Ingen porder-entry observeret i batch; ordre-kontekst er ukendt.");
        }

        var measurement = new MeasurementReceived
        {
            CorrelationId = correlationId,
            ChannelId = payload.ChannelId,
            WindowStart = payload.WindowStart,
            WindowEnd = payload.WindowEnd,
            ProducedUnits = producedUnits,
            RuntimeSeconds = runtimeSec,
            TotalDowntimeMinutes = totalDowntime,
            TopReason = topReason,
            OrderId = orderId,
            OrderTarget = orderTarget,
            CompletionPct = completionPct,
            StopReasons = aggregates
        };

        // Trin 5: Individuelle operatør-kommentarer udstedes uændret pr.
        // Trendlog-stoptime-entry, så Phase 4-AI-klassifikation har én post
        // pr. fri-tekstforekomst. Hvis Trendlog leverede et SourcePointId,
        // udledes StopEventId deterministisk via SHA-1, hvilket gør eventet
        // idempotent ved gentagne kald af /ingestion/trendlog/ingest-live.
        var comments = payload.Feeds.Stoptime
            .Where(s => !string.IsNullOrWhiteSpace(s.Reason))
            .Select(s => new OperatorCommentRegistered
            {
                StopEventId = DeriveStopEventId(s),
                CorrelationId = correlationId,
                ChannelId = payload.ChannelId,
                Timestamp = s.Timestamp,
                Reason = s.Reason.Trim(),
                Category = s.Category,
                DurationMinutes = s.DurationMinutes,
                OrderId = orderId
            })
            .ToList();

        if (anomalies.Count > 0)
        {
            _logger.LogWarning(
                "Normalisering fuldført med {AnomalyCount} dataanomalier for kanal {ChannelId}",
                anomalies.Count, payload.ChannelId);
        }

        _logger.LogInformation(
            "Normalisering fuldført. Kanal={ChannelId} ProducedUnits={Produced} RuntimeSec={Runtime} "
                + "TotalDowntimeMin={Downtime} TopReason={TopReason} OrderId={OrderId} "
                + "CompletionPct={Completion} Comments={CommentCount} CorrelationId={CorrelationId}",
            payload.ChannelId, producedUnits, runtimeSec, totalDowntime, topReason ?? "-",
            orderId ?? "-", completionPct?.ToString("F2") ?? "-", comments.Count, correlationId);

        return new NormalizationResult(measurement, comments, anomalies);
    }

    /// <summary>
    /// Udleder en stabil <see cref="Guid"/> for et stop-event på basis af
    /// Trendlogs <c>pointid</c> og tidsstempel. Algoritmen tager de første
    /// 16 bytes af en SHA-256-hash, hvilket sikrer at gentagne kald af
    /// ingest-endpointet for samme tidsvindue producerer identiske
    /// <c>StopEventId</c>'er — en forudsætning for idempotens i nedstrøms-
    /// systemerne (Analyzer, AI). SHA-256 er valgt frem for SHA-1 (UUIDv5)
    /// for at undgå analyzer-fejl <c>CA5350</c>; den deterministiske egenskab
    /// er uændret.
    /// Når Trendlog ikke leverer et pointid (fx for simulerede payloads),
    /// tildeles en ny tilfældig Guid.
    /// </summary>
    private static Guid DeriveStopEventId(TrendlogStopEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.SourcePointId))
        {
            return Guid.NewGuid();
        }

        var input = string.Concat(
            entry.SourcePointId,
            "|",
            entry.Timestamp.UtcDateTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture));

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        var bytes = new byte[16];
        Array.Copy(hash, bytes, 16);

        // RFC 4122-markører: pseudo-version 5 og DCE-variant. Værdien er
        // ikke en kanonisk UUIDv5 (som ville kræve SHA-1 og namespace), men
        // overholder bit-layoutet, så Postgres' uuid-type accepterer den.
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);

        return new Guid(bytes);
    }

    private (int Sum, IReadOnlyList<string> Anomalies) SumDiffs(
        IReadOnlyList<TrendlogDiffEntry> entries, string feedName)
    {
        var anomalies = new List<string>();
        var sum = 0;
        foreach (var entry in entries)
        {
            if (entry.Diff < 0)
            {
                var msg = $"Negativt diff-tal observeret i feed {feedName} ({entry.Diff}) ved {entry.Timestamp:O} – afvist.";
                anomalies.Add(msg);
                _logger.LogWarning("Dataanomali: {Anomaly}", msg);
                continue;
            }

            sum += entry.Diff;
        }

        return (sum, anomalies);
    }
}

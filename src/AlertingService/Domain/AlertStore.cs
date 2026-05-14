using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Pba.Shared.Contracts.V1;

namespace AlertingService.Domain;

/// <summary>
/// Tråd-sikker, in-memory ring-buffer over de seneste konsoliderede alarmer.
/// Holder en kort, fast historik (default 50) og fungerer samtidig som pub/sub-
/// kanal for SSE-abonnenter. Alarmer joines via <c>CorrelationId</c> imellem
/// <see cref="CriticalAlertTriggered"/> og <see cref="StopReasonClassified"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Designvalg.</b> En ægte read-model i en relationel tabel er bevidst
/// fravalgt i Phase 4. Kravet er, at frontend'en ser de seneste 50 alarmer i
/// det aktuelle udsnit, og at en SSE-stream skubber nye alarmer ud i realtid.
/// En ring-buffer i RAM er den simpleste og hurtigste opfyldelse af det krav.
/// Ulempen — at alarmhistorikken nulstilles ved restart — accepteres som en
/// bevidst begrænsning og dokumenteres i fase-rapporten.
/// </para>
/// <para>
/// <b>Trådsikkerhed.</b> En ekstern <see cref="ReaderWriterLockSlim"/> ville
/// være over-engineering for de små collection-størrelser; i stedet anvendes
/// en intern lock, hvor alle skriveoperationer er O(1) eller amortized O(1).
/// </para>
/// </remarks>
public sealed class AlertStore : IDisposable
{
    private readonly int _capacity;
    private readonly ILogger<AlertStore> _logger;
    private readonly object _gate = new();
    private readonly LinkedList<ConsolidatedAlert> _ring = new();
    private readonly Dictionary<Guid, LinkedListNode<ConsolidatedAlert>> _byCorrelation = new();

    // Subscribers er registreret pr. SSE-forbindelse. Hver subscriber er
    // en bounded channel, så langsomme klienter ikke kan opbygge ubegrænset
    // hukommelsestryk på serveren.
    private readonly ConcurrentDictionary<Guid, Channel<ConsolidatedAlert>> _subscribers = new();

    public AlertStore(ILogger<AlertStore> logger, int capacity = 50)
    {
        _logger = logger;
        _capacity = capacity;
    }

    /// <summary>
    /// Udfører en aktuel snapshot af ring-bufferen i kronologisk rækkefølge
    /// (ældste først). Anvendes ved SSE-tilkoblingens initial-replay.
    /// </summary>
    public IReadOnlyList<ConsolidatedAlert> Snapshot()
    {
        lock (_gate)
        {
            return _ring.ToArray();
        }
    }

    /// <summary>
    /// Registrerer en kritisk alarm. Hvis en AI-klassifikation for samme
    /// <c>CorrelationId</c> allerede er ankommet, sammenflettes felterne
    /// straks; ellers afventes klassifikationen og det indeværende objekt
    /// publiceres "uden AI-felter" til abonnenter.
    /// </summary>
    /// <remarks>
    /// Hvis en placeholder bærer AI-felter fra en tidligere
    /// <see cref="StopReasonClassified"/> hvis <c>OriginalReason</c> IKKE
    /// matcher den nu kendte <c>TopReason</c>, nulstilles AI-felterne.
    /// Det sikrer, at den AI-klassifikation der vinder visningen, altid
    /// hører til batchens dominerende stop-event (TopReason). Hele
    /// audit-historikken er fortsat bevaret i
    /// <c>classified_stop_reasons</c>-tabellen og kan tilgås via
    /// <c>GET /alerts/{correlationId}/classifications</c>.
    /// </remarks>
    public ConsolidatedAlert Upsert(CriticalAlertTriggered critical)
    {
        ArgumentNullException.ThrowIfNull(critical);

        ConsolidatedAlert consolidated;
        lock (_gate)
        {
            if (_byCorrelation.TryGetValue(critical.CorrelationId, out var existingNode))
            {
                // En tidligere AI-klassifikation har allerede skabt en placeholder-
                // række. Berig den med kritisk-alarm-felterne — og afgør om de
                // tentativt-merge'de AI-felter stadig hører til den vindende
                // TopReason. Hvis ikke, nulstilles de og afventer en matchende
                // StopReasonClassified.
                var existing = existingNode.Value;
                var keepAi = existing.AiOriginalReason is not null
                    && string.Equals(existing.AiOriginalReason, critical.TopReason,
                                     StringComparison.Ordinal);

                consolidated = existing with
                {
                    AlertId = critical.AlertId,
                    Timestamp = critical.OccurredAt,
                    ChannelId = critical.ChannelId,
                    Severity = critical.Severity,
                    Rule = critical.Rule,
                    Description = critical.Description,
                    TotalDowntimeMinutes = critical.TotalDowntimeMinutes,
                    TopReason = critical.TopReason,
                    OrderId = critical.OrderId,

                    // Nulstil AI-felter hvis de ikke matchede vindende TopReason.
                    AiCategory = keepAi ? existing.AiCategory : null,
                    AiSubcategory = keepAi ? existing.AiSubcategory : null,
                    AiStandardizedReason = keepAi ? existing.AiStandardizedReason : null,
                    AiSeverity = keepAi ? existing.AiSeverity : null,
                    AiRecommendedAction = keepAi ? existing.AiRecommendedAction : null,
                    AiConfidence = keepAi ? existing.AiConfidence : null,
                    AiLatencyMs = keepAi ? existing.AiLatencyMs : null,
                    AiIsFallback = keepAi ? existing.AiIsFallback : null,
                    AiOriginalReason = keepAi ? existing.AiOriginalReason : null
                };
                existingNode.Value = consolidated;
                MoveToHead(existingNode);
            }
            else
            {
                consolidated = new ConsolidatedAlert
                {
                    AlertId = critical.AlertId,
                    CorrelationId = critical.CorrelationId,
                    Timestamp = critical.OccurredAt,
                    ChannelId = critical.ChannelId,
                    Severity = critical.Severity,
                    Rule = critical.Rule,
                    Description = critical.Description,
                    TotalDowntimeMinutes = critical.TotalDowntimeMinutes,
                    TopReason = critical.TopReason,
                    OrderId = critical.OrderId
                };
                AddNew(consolidated);
            }
        }

        Broadcast(consolidated);
        return consolidated;
    }

    /// <summary>
    /// Registrerer en AI-klassifikation. AI-felterne merges KUN ind i den
    /// konsoliderede alarm hvis klassifikationens <c>OriginalReason</c> matcher
    /// alarmens <c>TopReason</c> — altså hvis dette stop-event er den
    /// tidsmæssigt dominerende årsag i batchen.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Match-semantik.</b> En batch kan indeholde mange stop-events under
    /// samme <c>CorrelationId</c>. Tidligere overskrev hver klassifikation
    /// blindt de foregående AI-felter, hvilket producerede en inkonsistent
    /// visning, hvor <c>TopReason</c> og <c>AiCategory</c> kunne beskrive
    /// to forskellige fysiske stop-events. Match-logikken nedenfor sikrer,
    /// at netop den AI-klassifikation der hører til <c>TopReason</c>,
    /// vinder visningen.
    /// </para>
    /// <para>
    /// <b>Audit-bevarelse.</b> Alle indkomne <c>StopReasonClassified</c>-events
    /// persisteres som rækker i tabellen <c>classified_stop_reasons</c>
    /// (jf. <see cref="Consumers.StopReasonClassifiedConsumer"/>) inden
    /// denne metode kaldes. Hele klassifikations-historikken for en batch
    /// kan dermed efterforskes via
    /// <c>GET /alerts/{correlationId}/classifications</c>.
    /// </para>
    /// <para>
    /// <b>Race-håndtering.</b> Hvis ingen kritisk alarm (og dermed ingen
    /// <c>TopReason</c>) endnu er ankommet, skabes en placeholder med
    /// klassifikationens AI-felter som tentative værdier — og
    /// <c>AiOriginalReason</c> sat til <c>classified.OriginalReason</c>.
    /// Når den efterfølgende <c>CriticalAlertTriggered</c> ankommer, vurderer
    /// <see cref="Upsert(CriticalAlertTriggered)"/> om de tentative felter
    /// skal beholdes (match) eller nulstilles (mismatch).
    /// </para>
    /// </remarks>
    public ConsolidatedAlert Upsert(StopReasonClassified classified)
    {
        ArgumentNullException.ThrowIfNull(classified);

        ConsolidatedAlert consolidated;
        bool merged;
        lock (_gate)
        {
            if (_byCorrelation.TryGetValue(classified.CorrelationId, out var existingNode))
            {
                var existing = existingNode.Value;

                // Match-regel: AI-felterne vinder visningen kun hvis dette
                // klassificerede stop-event hører til batchens dominerende
                // TopReason. Hvis TopReason endnu er ukendt (placeholder-
                // tilstand, hvor kritisk alarm endnu ikke er ankommet),
                // accepteres klassifikationen tentativt — den vil blive
                // re-evalueret af CriticalAlertTriggered-handleren.
                var topReasonKnown = !string.IsNullOrEmpty(existing.TopReason)
                    && !string.Equals(existing.Rule, "PendingClassification", StringComparison.Ordinal);

                var isMatch = topReasonKnown
                    ? string.Equals(existing.TopReason, classified.OriginalReason, StringComparison.Ordinal)
                    : true;

                if (isMatch)
                {
                    consolidated = existing with
                    {
                        AiCategory = classified.Category,
                        AiSubcategory = classified.Subcategory,
                        AiStandardizedReason = classified.StandardizedReason,
                        AiSeverity = classified.Severity,
                        AiRecommendedAction = classified.RecommendedAction,
                        AiConfidence = classified.Confidence,
                        AiLatencyMs = classified.LatencyMs,
                        AiIsFallback = classified.IsFallback,
                        AiOriginalReason = classified.OriginalReason
                    };
                    existingNode.Value = consolidated;
                    merged = true;
                }
                else
                {
                    // Ingen overskrivning — den eksisterende AI-projektion
                    // for TopReason er rigtig og bibeholdes. Klassifikationen
                    // er i forvejen persisteret i classified_stop_reasons og
                    // tilgængelig via audit-endpointet.
                    consolidated = existing;
                    merged = false;
                }
            }
            else
            {
                // Placeholder: AI-klassifikationen ankom før den kritiske alarm.
                // Felter relateret til kritikalitet udfyldes med default-værdier
                // og re-evalueres, så snart den tilhørende kritiske alarm
                // ankommer (jf. Upsert(CriticalAlertTriggered)).
                consolidated = new ConsolidatedAlert
                {
                    AlertId = Guid.Empty,
                    CorrelationId = classified.CorrelationId,
                    Timestamp = classified.OccurredAt,
                    ChannelId = classified.ChannelId,
                    Severity = "Unknown",
                    Rule = "PendingClassification",
                    Description = "Afventer kritisk alarm for samme korrelations-ID.",
                    TotalDowntimeMinutes = 0,
                    TopReason = classified.OriginalReason,
                    AiCategory = classified.Category,
                    AiSubcategory = classified.Subcategory,
                    AiStandardizedReason = classified.StandardizedReason,
                    AiSeverity = classified.Severity,
                    AiRecommendedAction = classified.RecommendedAction,
                    AiConfidence = classified.Confidence,
                    AiLatencyMs = classified.LatencyMs,
                    AiIsFallback = classified.IsFallback,
                    AiOriginalReason = classified.OriginalReason
                };
                AddNew(consolidated);
                merged = true;
            }
        }

        // Kun broadcast hvis visningen rent faktisk ændrede sig. En
        // klassifikation der ikke matcher TopReason er en no-op for
        // SSE-strømmen, men er i forvejen persisteret som audit-row.
        if (merged)
        {
            Broadcast(consolidated);
        }
        return consolidated;
    }

    /// <summary>
    /// Registrerer en SSE-abonnent. Returnerer både en læser og et engangs-
    /// dispose-objekt, som SSE-endpointet skal kalde, når forbindelsen lukkes,
    /// for at frigive abonnementet.
    /// </summary>
    public (ChannelReader<ConsolidatedAlert> Reader, IDisposable Subscription) Subscribe()
    {
        var channel = Channel.CreateBounded<ConsolidatedAlert>(new BoundedChannelOptions(capacity: 64)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });

        var id = Guid.NewGuid();
        _subscribers[id] = channel;
        _logger.LogInformation("SSE-subscriber tilkoblet. SubscriberId={SubscriberId} TotalSubscribers={Count}",
            id, _subscribers.Count);

        return (channel.Reader, new SubscriptionLease(this, id));
    }

    private void Broadcast(ConsolidatedAlert alert)
    {
        foreach (var (id, channel) in _subscribers)
        {
            if (!channel.Writer.TryWrite(alert))
            {
                _logger.LogWarning(
                    "SSE-subscriber kunne ikke modtage broadcast (kanal fyldt). SubscriberId={SubscriberId}", id);
            }
        }
    }

    private void Unsubscribe(Guid id)
    {
        if (_subscribers.TryRemove(id, out var channel))
        {
            channel.Writer.TryComplete();
            _logger.LogInformation(
                "SSE-subscriber afkoblet. SubscriberId={SubscriberId} TotalSubscribers={Count}",
                id, _subscribers.Count);
        }
    }

    private void AddNew(ConsolidatedAlert alert)
    {
        var node = new LinkedListNode<ConsolidatedAlert>(alert);
        _ring.AddFirst(node);
        _byCorrelation[alert.CorrelationId] = node;

        while (_ring.Count > _capacity)
        {
            var oldest = _ring.Last!;
            _ring.RemoveLast();
            _byCorrelation.Remove(oldest.Value.CorrelationId);
        }
    }

    private void MoveToHead(LinkedListNode<ConsolidatedAlert> node)
    {
        if (_ring.First == node) return;
        _ring.Remove(node);
        _ring.AddFirst(node);
    }

    public void Dispose()
    {
        foreach (var channel in _subscribers.Values)
        {
            channel.Writer.TryComplete();
        }
        _subscribers.Clear();
    }

    private sealed class SubscriptionLease : IDisposable
    {
        private readonly AlertStore _owner;
        private readonly Guid _id;
        private bool _disposed;

        public SubscriptionLease(AlertStore owner, Guid id)
        {
            _owner = owner;
            _id = id;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _owner.Unsubscribe(_id);
        }
    }
}

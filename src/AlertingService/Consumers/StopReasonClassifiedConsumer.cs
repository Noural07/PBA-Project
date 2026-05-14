using System;
using System.Threading.Tasks;
using AlertingService.Domain;
using AlertingService.Persistence;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Pba.Shared.Contracts.V1;

namespace AlertingService.Consumers;

/// <summary>
/// Konsumerer <see cref="StopReasonClassified"/>-events fra RabbitMQ. Eventet
/// persisteres først til Postgres som auditerbar log
/// (<see cref="ClassifiedStopReason"/>) og beriger derefter den
/// korresponderende konsoliderede alarm i ring-bufferen. Hvis den
/// kritiske alarm endnu ikke er ankommet, oprettes en placeholder-række, der
/// senere bliver komplet, når <c>CriticalAlertTriggered</c> arriverer for
/// samme korrelations-ID.
/// </summary>
/// <remarks>
/// <para>
/// <b>Idempotens.</b> Tabellen <c>classified_stop_reasons</c> har et unikt
/// indeks på <c>StopEventId</c>. Et duplikat-event (eksempelvis fra MassTransit's
/// retry-pipeline) afvises derfor på databaseniveau med en
/// <see cref="DbUpdateException"/>, som her fanges og logges som information,
/// hvorefter ring-bufferen alligevel opdateres for at sikre at SSE-frontend'en
/// modtager den seneste klassifikation.
/// </para>
/// <para>
/// <b>Fejl-isolering.</b> Hvis Postgres er midlertidigt utilgængeligt,
/// propageres exceptionen op i MassTransit's retry-pipeline (3 forsøg
/// med eksponentiel backoff, jf. <c>MassTransitRegistration</c>). Først
/// hvis alle retries fejler, sendes eventet til broker'ens dead-letter
/// kø. Frontend'ens visning afhænger ikke af persistensens succes —
/// ring-bufferen opdateres uafhængigt.
/// </para>
/// </remarks>
public sealed class StopReasonClassifiedConsumer : IConsumer<StopReasonClassified>
{
    private readonly AlertStore _store;
    private readonly AlertingDbContext _db;
    private readonly ILogger<StopReasonClassifiedConsumer> _logger;

    public StopReasonClassifiedConsumer(
        AlertStore store,
        AlertingDbContext db,
        ILogger<StopReasonClassifiedConsumer> logger)
    {
        _store = store;
        _db = db;
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<StopReasonClassified> context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var message = context.Message;

        using var correlationScope = Serilog.Context.LogContext.PushProperty(
            "correlationId", message.CorrelationId);
        using var stopScope = Serilog.Context.LogContext.PushProperty(
            "stopEventId", message.StopEventId);

        // Persistens før broadcast: hvis databasen er midlertidigt utilgængelig,
        // ønskes en MassTransit-retry frem for at sende et event til frontend'en
        // som så aldrig ender i auditeringen. Idempotens er sikret via det unikke
        // indeks på StopEventId i AlertingDbContext.
        await PersistAsync(message, context.CancellationToken);

        var consolidated = _store.Upsert(message);

        // Match-status: AlertStore merger kun AI-felter ind hvis dette
        // stop-events OriginalReason matcher alarmens TopReason. Vi udleder
        // status fra den returnerede projektion, så loggen tydeligt skelner
        // mellem en "vindende" klassifikation (visningen er opdateret) og en
        // "audit-only" klassifikation (kun persisteret i Postgres).
        var matched = string.Equals(
            consolidated.AiOriginalReason,
            message.OriginalReason,
            StringComparison.Ordinal);

        if (matched)
        {
            _logger.LogWarning(
                "ConsolidatedAlert opdateret (AI-match for TopReason). AlertId={AlertId} "
                    + "OriginalReason='{OriginalReason}' AiCategory={AiCategory} "
                    + "AiSubcategory={AiSubcategory} AiSeverity={AiSeverity} "
                    + "AiStandardizedReason='{AiReason}' AiConfidence={AiConfidence:F2} "
                    + "AiLatencyMs={AiLatencyMs} IsFallback={IsFallback}",
                consolidated.AlertId,
                message.OriginalReason,
                consolidated.AiCategory ?? "-",
                consolidated.AiSubcategory ?? "-",
                consolidated.AiSeverity ?? "-",
                consolidated.AiStandardizedReason ?? "-",
                consolidated.AiConfidence ?? double.NaN,
                consolidated.AiLatencyMs ?? -1L,
                consolidated.AiIsFallback ?? false);
        }
        else
        {
            _logger.LogInformation(
                "AI-klassifikation persisteret (audit-only — ikke matchende TopReason). "
                    + "AlertId={AlertId} TopReason='{TopReason}' "
                    + "OriginalReason='{OriginalReason}' Category={Category}",
                consolidated.AlertId,
                consolidated.TopReason ?? "-",
                message.OriginalReason,
                message.Category);
        }
    }

    private async Task PersistAsync(StopReasonClassified message, System.Threading.CancellationToken cancellationToken)
    {
        var entity = new ClassifiedStopReason
        {
            EventId = message.EventId,
            CorrelationId = message.CorrelationId,
            StopEventId = message.StopEventId,
            OccurredAt = message.OccurredAt,
            ChannelId = message.ChannelId,
            OriginalReason = Truncate(message.OriginalReason, 2048),
            Category = Truncate(message.Category, 32),
            Subcategory = Truncate(message.Subcategory, 64),
            StandardizedReason = Truncate(message.StandardizedReason, 128),
            Severity = Truncate(message.Severity, 16),
            RecommendedAction = Truncate(message.RecommendedAction, 128),
            Confidence = message.Confidence,
            LatencyMs = message.LatencyMs,
            IsFallback = message.IsFallback
        };

        _db.ClassifiedStopReasons.Add(entity);
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
            _logger.LogInformation(
                "ClassifiedStopReason persisteret. RowId={RowId} StopEventId={StopEventId}",
                entity.Id, entity.StopEventId);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // Duplikat — formentlig en MassTransit-genleverance. Loggen er
            // bevidst på Information-niveau, da det er en forventet,
            // ikke-kritisk hændelse i et idempotent design.
            _db.Entry(entity).State = EntityState.Detached;
            _logger.LogInformation(
                "ClassifiedStopReason allerede persisteret (idempotent skip). StopEventId={StopEventId}",
                entity.StopEventId);
        }
    }

    /// <summary>
    /// Identificerer Postgres' unique-violation (SQLSTATE 23505) gennem
    /// inner exception-kæden, uden at tage en hård dependency på Npgsql.
    /// </summary>
    private static bool IsUniqueViolation(DbUpdateException exception)
    {
        var current = exception.InnerException;
        while (current is not null)
        {
            // Postgres-specifik SQLSTATE bæres typisk af PostgresException's
            // SqlState-felt. Reflection-tilgangen undgår en hård kompil-tidsafhængighed
            // af Npgsql i denne fil.
            var sqlStateProp = current.GetType().GetProperty("SqlState");
            if (sqlStateProp?.GetValue(current) is string sqlState
                && string.Equals(sqlState, "23505", StringComparison.Ordinal))
            {
                return true;
            }

            current = current.InnerException;
        }

        return false;
    }

    private static string Truncate(string value, int maxLength)
        => string.IsNullOrEmpty(value)
            ? string.Empty
            : value.Length <= maxLength ? value : value[..maxLength];
}

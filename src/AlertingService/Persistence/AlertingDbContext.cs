using Microsoft.EntityFrameworkCore;

namespace AlertingService.Persistence;

/// <summary>
/// EF Core DbContext for AlertingService' audit-kontekst. Indeholder
/// for nuværende ét aggregat — <see cref="ClassifiedStopReasons"/> — som
/// langtidsholdbart auditerbart spor over alle AI-klassifikationer
/// streamet gennem systemet.
/// </summary>
/// <remarks>
/// Konteksten er en bevidst smal projektion: domæneaggregaterne for
/// kritiske alarmer ejes fortsat af <c>AnalyzerService</c>, og
/// AlertingService persisterer udelukkende det der ikke i forvejen
/// er ejet af en anden service. Dette undgår dobbeltlagring og
/// holder bounded contexts adskilt.
/// </remarks>
public sealed class AlertingDbContext : DbContext
{
    public AlertingDbContext(DbContextOptions<AlertingDbContext> options)
        : base(options)
    {
    }

    public DbSet<ClassifiedStopReason> ClassifiedStopReasons => Set<ClassifiedStopReason>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<ClassifiedStopReason>(entity =>
        {
            entity.ToTable("classified_stop_reasons");
            entity.HasKey(c => c.Id);
            entity.Property(c => c.Id).UseIdentityAlwaysColumn();

            entity.Property(c => c.OriginalReason).HasMaxLength(2048).IsRequired();
            entity.Property(c => c.Category).HasMaxLength(32).IsRequired();
            entity.Property(c => c.Subcategory).HasMaxLength(64).IsRequired();
            entity.Property(c => c.StandardizedReason).HasMaxLength(128).IsRequired();
            entity.Property(c => c.Severity).HasMaxLength(16).IsRequired();
            entity.Property(c => c.RecommendedAction).HasMaxLength(128).IsRequired();

            // StopEventId udnyttes som idempotens-nøgle for at sikre, at
            // en eventuel genleverance fra MassTransit's retry-pipeline
            // ikke skaber dubletter i audit-tabellen.
            entity.HasIndex(c => c.StopEventId).IsUnique();
            entity.HasIndex(c => c.CorrelationId);
            entity.HasIndex(c => new { c.ChannelId, c.OccurredAt });
        });
    }
}

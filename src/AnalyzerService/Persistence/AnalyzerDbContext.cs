using AnalyzerService.Domain;
using Microsoft.EntityFrameworkCore;

namespace AnalyzerService.Persistence;

/// <summary>
/// EF Core DbContext for analysekonteksten. Anvender PostgreSQL via
/// Npgsql-provideren og indeholder de tre aggregater
/// <see cref="Measurements"/>, <see cref="StopEvents"/> og
/// <see cref="CriticalAlerts"/>. Konfigurationen overholder
/// nullable-reference-types-modellen i .NET 9 og anvender
/// <c>HasIndex</c> til at sikre praktisk brugbare opslag (efter
/// ordreId og korrelationsId).
/// </summary>
public sealed class AnalyzerDbContext : DbContext
{
    public AnalyzerDbContext(DbContextOptions<AnalyzerDbContext> options)
        : base(options)
    {
    }

    public DbSet<Measurement> Measurements => Set<Measurement>();

    public DbSet<StopEvent> StopEvents => Set<StopEvent>();

    public DbSet<CriticalAlert> CriticalAlerts => Set<CriticalAlert>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<Measurement>(entity =>
        {
            entity.ToTable("measurements");
            entity.HasKey(m => m.Id);
            entity.Property(m => m.TopReason).HasMaxLength(512);
            entity.Property(m => m.OrderId).HasMaxLength(128);
            entity.HasIndex(m => m.CorrelationId);
            entity.HasIndex(m => m.OrderId);
            entity.HasIndex(m => new { m.ChannelId, m.WindowStart });

            entity.HasMany(m => m.StopEvents)
                .WithOne(s => s.Measurement!)
                .HasForeignKey(s => s.MeasurementId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(m => m.CriticalAlert)
                .WithOne(c => c.Measurement!)
                .HasForeignKey<CriticalAlert>(c => c.MeasurementId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<StopEvent>(entity =>
        {
            entity.ToTable("stop_events");
            entity.HasKey(s => s.Id);
            entity.Property(s => s.Reason).HasMaxLength(1024).IsRequired();
            entity.HasIndex(s => s.MeasurementId);
        });

        modelBuilder.Entity<CriticalAlert>(entity =>
        {
            entity.ToTable("critical_alerts");
            entity.HasKey(c => c.Id);
            entity.Property(c => c.Severity).HasMaxLength(32).IsRequired();
            entity.Property(c => c.Rule).HasMaxLength(128).IsRequired();
            entity.Property(c => c.TriggeredRules).HasMaxLength(512);
            entity.Property(c => c.Description).HasMaxLength(2048);
            entity.Property(c => c.TopReason).HasMaxLength(512);
            entity.Property(c => c.ObservedCriticalReasons).HasMaxLength(2048);
            entity.HasIndex(c => c.CorrelationId);
            entity.HasIndex(c => c.MeasurementId).IsUnique();
        });
    }
}

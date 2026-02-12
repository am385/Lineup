using Lineup.Core.Storage.Entities;
using Microsoft.EntityFrameworkCore;

namespace Lineup.Core.Storage;

/// <summary>
/// Database context for EPG data storage
/// Uses SQLite for lightweight local caching
/// </summary>
public class EpgDbContext : DbContext
{
    public DbSet<StoredChannel> Channels { get; set; }
    public DbSet<StoredProgram> Programs { get; set; }

    public EpgDbContext(DbContextOptions<EpgDbContext> options) : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Index for efficient channel lookups
        modelBuilder.Entity<StoredChannel>()
            .HasIndex(c => c.GuideNumber)
            .IsUnique();

        // Indexes for efficient program queries
        modelBuilder.Entity<StoredProgram>()
            .HasIndex(p => p.GuideNumber);

        modelBuilder.Entity<StoredProgram>()
            .HasIndex(p => p.StartTime);

        modelBuilder.Entity<StoredProgram>()
            .HasIndex(p => p.EndTime);

        // Composite index for duplicate checking
        modelBuilder.Entity<StoredProgram>()
            .HasIndex(p => new { p.GuideNumber, p.StartTime, p.Title });
    }
}

using Lineup.Core.Storage.Entities;
using Microsoft.EntityFrameworkCore;

namespace Lineup.Core.Storage;

/// <summary>
/// Database context for EPG data storage
/// Uses SQLite for lightweight local caching
/// </summary>
public class EpgDbContext : DbContext
{
    /// <summary>
    /// Gets or sets channels.
    /// </summary>
    public DbSet<StoredChannel> Channels { get; set; }

    /// <summary>
    /// Gets or sets programs.
    /// </summary>
    public DbSet<StoredProgram> Programs { get; set; }

    /// <summary>
    /// Gets or sets physical tuner lineup channels.
    /// </summary>
    public DbSet<StoredLineupChannel> LineupChannels { get; set; }

    /// <summary>
    /// Gets or sets committed guide imports.
    /// </summary>
    public DbSet<StoredGuideImport> GuideImports { get; set; }

    /// <summary>
    /// Initializes a new instance of the <see cref="EpgDbContext"/> class.
    /// </summary>
    public EpgDbContext(DbContextOptions<EpgDbContext> options) : base(options)
    {
    }

    /// <summary>
    /// Performs the on model creating operation.
    /// </summary>
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
            .HasIndex(p => new { p.GuideNumber, p.StartTime, p.EndTime, p.Title })
            .IsUnique();

        modelBuilder.Entity<StoredLineupChannel>()
            .HasIndex(channel => channel.GuideNumber)
            .IsUnique();

        modelBuilder.Entity<StoredLineupChannel>()
            .HasIndex(channel => new { channel.IsActive, channel.IsEnabled });

        modelBuilder.Entity<StoredGuideImport>()
            .HasIndex(import => import.CompletedUtc);
    }
}

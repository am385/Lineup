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
    /// Gets or sets provider-neutral physical source channels.
    /// </summary>
    public DbSet<StoredPhysicalSourceChannel> PhysicalSourceChannels { get; set; }

    /// <summary>
    /// Gets or sets guide source channels.
    /// </summary>
    public DbSet<StoredGuideSourceChannel> GuideSourceChannels { get; set; }

    /// <summary>
    /// Gets or sets authoritative source-to-guide channel bindings.
    /// </summary>
    public DbSet<StoredSourceGuideBinding> SourceGuideBindings { get; set; }

    /// <summary>
    /// Gets or sets normalized programs from guide sources.
    /// </summary>
    public DbSet<StoredGuideSourceProgram> GuideSourcePrograms { get; set; }

    /// <summary>
    /// Gets or sets the last successful imports for guide sources.
    /// </summary>
    public DbSet<StoredDeviceGuideImport> DeviceGuideImports { get; set; }

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

        modelBuilder.Entity<StoredPhysicalSourceChannel>()
            .HasIndex(channel => new { channel.PhysicalDeviceId, channel.ProviderKey })
            .IsUnique();

        modelBuilder.Entity<StoredPhysicalSourceChannel>()
            .HasIndex(channel => new { channel.PhysicalDeviceId, channel.IsActive });

        modelBuilder.Entity<StoredPhysicalSourceChannel>()
            .HasIndex(channel => new { channel.IsActive, channel.IsEnabled });

        modelBuilder.Entity<StoredGuideSourceChannel>()
            .HasIndex(channel => new { channel.GuideSourceId, channel.ProviderKey })
            .IsUnique();

        modelBuilder.Entity<StoredSourceGuideBinding>()
            .HasIndex(binding => binding.PhysicalSourceChannelId)
            .IsUnique();

        modelBuilder.Entity<StoredGuideSourceProgram>()
            .HasIndex(program => new { program.GuideSourceChannelId, program.StartTime, program.EndTime, program.Title })
            .IsUnique();

        modelBuilder.Entity<StoredDeviceGuideImport>()
            .HasIndex(import => import.GuideSourceId)
            .IsUnique();

    }
}

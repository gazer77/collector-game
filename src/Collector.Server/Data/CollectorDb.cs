using Microsoft.EntityFrameworkCore;

namespace Collector.Server.Data;

public sealed class CollectorDb(DbContextOptions<CollectorDb> options) : DbContext(options)
{
    public DbSet<Player> Players => Set<Player>();
    public DbSet<Save> Saves => Set<Save>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<Player>(e =>
        {
            e.HasIndex(p => p.DeviceId).IsUnique();
            e.Property(p => p.DeviceId).HasMaxLength(128);
        });
        model.Entity<Save>(e =>
        {
            e.HasKey(s => new { s.PlayerId, s.PackId });
            e.Property(s => s.PackId).HasMaxLength(64);
            e.Property(s => s.Version).IsConcurrencyToken();
            e.HasOne<Player>().WithMany().HasForeignKey(s => s.PlayerId);
        });
    }
}

public sealed class Player
{
    public Guid Id { get; set; }

    /// <summary>The guest credential. Account linking (email/OAuth) will add other ways to sign in.</summary>
    public required string DeviceId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>One player's game in one pack. The game state is stored as JSON; it's only ever loaded whole.</summary>
public sealed class Save
{
    public Guid PlayerId { get; set; }
    public required string PackId { get; set; }
    public required string StateJson { get; set; }
    public long Version { get; set; }

    /// <summary>Wall-clock time of the player's last request, used to advance the game clock.</summary>
    public DateTimeOffset LastSeenAt { get; set; }

    /// <summary>Game time granted ahead of the wall clock (clock-drift tolerance), repaid from later catch-ups.</summary>
    public long AheadMs { get; set; }
}

using KoFFBot.Domain;
using Microsoft.EntityFrameworkCore;

namespace KoFFBot.Data;

public class GameDbContext : DbContext
{
    public DbSet<GameProfile> GameProfiles { get; set; } = null!;
    public DbSet<LeaderboardRecord> LeaderboardRecords { get; set; } = null!;
    public DbSet<TelegramUser> TelegramUsers { get; set; } = null!; // Нужен для Join в лидеборде

    public GameDbContext(DbContextOptions<GameDbContext> options) : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.Entity<LeaderboardRecord>().HasIndex(l => l.MaxScore).IsDescending();
    }
}
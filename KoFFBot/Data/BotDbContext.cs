using KoFFBot.Domain;
using Microsoft.EntityFrameworkCore;

namespace KoFFBot.Data;

public class BotDbContext : DbContext
{
    public DbSet<TelegramUser> TelegramUsers { get; set; } = null!;
    public DbSet<VpnSubscription> VpnSubscriptions { get; set; } = null!;
    public DbSet<ServerTemplate> ServerTemplates { get; set; } = null!;
    public DbSet<SupportMessage> SupportMessages { get; set; } = null!;
    public DbSet<Referral> Referrals { get; set; } = null!;

    // === НОВЫЕ ТАБЛИЦЫ ДЛЯ ИГРЫ ===
    public DbSet<GameProfile> GameProfiles { get; set; } = null!;
    public DbSet<LeaderboardRecord> LeaderboardRecords { get; set; } = null!;

    public BotDbContext(DbContextOptions<BotDbContext> options) : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Индексы VPN
        modelBuilder.Entity<VpnSubscription>().HasIndex(s => s.TelegramId);
        modelBuilder.Entity<VpnSubscription>().HasIndex(s => s.SyncStatus);
        modelBuilder.Entity<SupportMessage>().HasIndex(m => m.TelegramId);

        // === ОПТИМИЗАЦИЯ ИГРОВОЙ БД (Правило 198) ===
        // Индекс для молниеносной сортировки таблицы лидеров
        modelBuilder.Entity<LeaderboardRecord>()
            .HasIndex(l => l.MaxScore)
            .IsDescending(); // Сортируем от большего к меньшему прямо на уровне базы
    }
}
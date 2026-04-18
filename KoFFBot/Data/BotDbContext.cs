using KoFFBot.Domain;
using Microsoft.EntityFrameworkCore;

namespace KoFFBot.Data;

public class BotDbContext : DbContext
{
    public DbSet<TelegramUser> TelegramUsers { get; set; } = null!;
    public DbSet<VpnSubscription> VpnSubscriptions { get; set; } = null!;
    public DbSet<ServerTemplate> ServerTemplates { get; set; } = null!;

    // Новые таблицы
    public DbSet<SupportMessage> SupportMessages { get; set; } = null!;
    public DbSet<Referral> Referrals { get; set; } = null!;

    public BotDbContext(DbContextOptions<BotDbContext> options) : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Индексы для быстрого поиска
        modelBuilder.Entity<VpnSubscription>()
            .HasIndex(s => s.TelegramId);

        modelBuilder.Entity<VpnSubscription>()
            .HasIndex(s => s.SyncStatus);

        // Индекс для чатов (чтобы быстро грузить Inbox юзера)
        modelBuilder.Entity<SupportMessage>()
            .HasIndex(m => m.TelegramId);
    }
}
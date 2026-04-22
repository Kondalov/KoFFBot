using KoFFBot.Domain;
using Microsoft.EntityFrameworkCore;

namespace KoFFBot.Data;

public class VpnDbContext : DbContext
{
    public DbSet<TelegramUser> TelegramUsers { get; set; } = null!;
    public DbSet<VpnSubscription> VpnSubscriptions { get; set; } = null!;
    public DbSet<ServerTemplate> ServerTemplates { get; set; } = null!;
    public DbSet<SupportMessage> SupportMessages { get; set; } = null!;
    public DbSet<Referral> Referrals { get; set; } = null!;

    public VpnDbContext(DbContextOptions<VpnDbContext> options) : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.Entity<VpnSubscription>().HasIndex(s => s.TelegramId);
        modelBuilder.Entity<VpnSubscription>().HasIndex(s => s.SyncStatus);
        modelBuilder.Entity<SupportMessage>().HasIndex(m => m.TelegramId);
    }
}
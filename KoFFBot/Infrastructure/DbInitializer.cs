using KoFFBot.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Builder;

namespace KoFFBot.Infrastructure;

public static class DbInitializer
{
    public static void InitializeDatabase(this WebApplication app)
    {
        using var scope = app.Services.CreateScope();
        
        // Инициализируем VPN DB
        var vpnDb = scope.ServiceProvider.GetRequiredService<VpnDbContext>();
        vpnDb.Database.EnsureCreated();
        vpnDb.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");

        // Инициализируем Game DB
        var gameDb = scope.ServiceProvider.GetRequiredService<GameDbContext>();
        gameDb.Database.EnsureCreated();
        gameDb.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");

        // Безопасное добавление колонок (для обратной совместимости, если база уже была)
        // В будущем рекомендуется перейти на полноценные миграции
        string[] gameAlterations = {
            "ALTER TABLE \"GameProfiles\" ADD COLUMN \"BossKills\" INTEGER NOT NULL DEFAULT 0;",
            "ALTER TABLE \"GameProfiles\" ADD COLUMN \"MonthlyBossKills\" INTEGER NOT NULL DEFAULT 0;",
            "ALTER TABLE \"GameProfiles\" ADD COLUMN \"LastBossKillDate\" TEXT NOT NULL DEFAULT '2000-01-01 00:00:00';",
            "ALTER TABLE \"GameProfiles\" ADD COLUMN \"LastDailyBonusDate\" TEXT NOT NULL DEFAULT '0001-01-01 00:00:00';",
            "ALTER TABLE \"GameProfiles\" ADD COLUMN \"CurrentGameStartTime\" TEXT NOT NULL DEFAULT '0001-01-01 00:00:00';"
        };

        foreach (var sql in gameAlterations)
        {
            try { gameDb.Database.ExecuteSqlRaw(sql); } catch { /* Ignore if exists */ }
        }
    }
}
using KoFFBot.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Builder;
using System;
using System.Linq;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Metadata;

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
        
        // Принудительно создаем таблицы GameDbContext вручную, так как БД общая с VpnDbContext
        string createGameProfiles = @"
            CREATE TABLE IF NOT EXISTS ""GameProfiles"" (
                ""TelegramId"" INTEGER NOT NULL CONSTRAINT ""PK_GameProfiles"" PRIMARY KEY,
                ""CurrentEnergy"" INTEGER NOT NULL,
                ""LastEnergyUpdate"" TEXT NOT NULL,
                ""BossKills"" INTEGER NOT NULL,
                ""MonthlyBossKills"" INTEGER NOT NULL,
                ""LastBossKillDate"" TEXT NOT NULL,
                ""LastDailyBonusDate"" TEXT NOT NULL,
                ""ClaimedReferralMilestone"" INTEGER NOT NULL,
                ""LastHappyHourDate"" TEXT NOT NULL,
                ""LastRetentionDate"" TEXT NOT NULL,
                ""CurrentGameStartTime"" TEXT NOT NULL,
                ""EnergySignature"" TEXT NOT NULL,
                ""IsBanned"" INTEGER NOT NULL,
                ""BanReason"" TEXT NOT NULL
            );";

        string createLeaderboard = @"
            CREATE TABLE IF NOT EXISTS ""LeaderboardRecords"" (
                ""TelegramId"" INTEGER NOT NULL CONSTRAINT ""PK_LeaderboardRecords"" PRIMARY KEY,
                ""MaxScore"" INTEGER NOT NULL,
                ""AchievedAt"" TEXT NOT NULL,
                ""ScoreSignature"" TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ""IX_LeaderboardRecords_MaxScore"" ON ""LeaderboardRecords"" (""MaxScore"" DESC);";

        gameDb.Database.ExecuteSqlRaw(createGameProfiles);
        gameDb.Database.ExecuteSqlRaw(createLeaderboard);
        
        gameDb.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");

        // === АВТОМАТИЧЕСКАЯ ДОБАВЛЕНИЕ КОЛОНОК (2026 СТАНДАРТ) ===
        // Умный алгоритм SQLite Runtime Auto-Migration
        EnsureColumnsExist(vpnDb);
        EnsureColumnsExist(gameDb);
    }

    private static void EnsureColumnsExist(DbContext dbContext)
    {
        var connection = dbContext.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
            connection.Open();

        // УМНЫЙ АЛГОРИТМ: Изолируем изменения в транзакции. Если ошибка - откатываем всё, БД не ломается.
        using var transaction = connection.BeginTransaction();
        try
        {
            var model = dbContext.Model;
            foreach (var entityType in model.GetEntityTypes())
            {
                var tableName = entityType.GetTableName();
                if (string.IsNullOrEmpty(tableName)) continue;

                using var command = connection.CreateCommand();
                command.CommandText = $"PRAGMA table_info(\"{tableName}\");";
                command.Transaction = transaction;

                using var reader = command.ExecuteReader();
                var existingColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                while (reader.Read())
                {
                    existingColumns.Add(reader.GetString(1));
                }
                reader.Close();

                foreach (var property in entityType.GetProperties())
                {
                    var columnName = property.GetColumnName(StoreObjectIdentifier.Table(tableName, null));
                    if (columnName != null && !existingColumns.Contains(columnName))
                    {
                        Serilog.Log.Information("[DB-MIGRATE] Добавление колонки {Column} в таблицу {Table}", columnName, tableName);

                        // Строгий маппинг типов под SQLite 
                        string columnType = property.ClrType == typeof(int) || property.ClrType == typeof(long) || property.ClrType == typeof(bool) ? "INTEGER" :
                                            property.ClrType == typeof(DateTime) || property.ClrType == typeof(DateTime?) ? "TEXT" :
                                            property.ClrType == typeof(double) || property.ClrType == typeof(float) ? "REAL" : "TEXT";

                        var defaultValue = property.ClrType == typeof(bool) ? "DEFAULT 0" :
                                           property.ClrType == typeof(int) || property.ClrType == typeof(long) ? "DEFAULT 0" :
                                           property.GetDefaultValue() != null ? $"DEFAULT '{property.GetDefaultValue()}'" :
                                           (property.IsNullable ? "DEFAULT NULL" : "DEFAULT ''");

                        var alterSql = $"ALTER TABLE \"{tableName}\" ADD COLUMN \"{columnName}\" {columnType} {defaultValue};";

                        using var alterCommand = connection.CreateCommand();
                        alterCommand.CommandText = alterSql;
                        alterCommand.Transaction = transaction;
                        alterCommand.ExecuteNonQuery();
                    }
                }
            }
            transaction.Commit();
        }
        catch (Exception ex)
        {
            transaction.Rollback();
            Serilog.Log.Error($"[DB-MIGRATE-ERROR] Критический сбой расширения БД. Изменения отменены: {ex.Message}");
        }
    }
}

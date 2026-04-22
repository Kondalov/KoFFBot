using KoFFBot.Data;
using KoFFBot.Domain;
using KoFFBot.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace KoFFBot.Services;

public class GameService
{
    private readonly GameDbContext _dbContext;
    private readonly ILogger<GameService> _logger;

    public GameService(GameDbContext dbContext, ILogger<GameService> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    public async Task<GameProfile> GetOrCreateProfileAsync(long telegramId, CancellationToken ct)
    {
        var profile = await _dbContext.GameProfiles.FirstOrDefaultAsync(p => p.TelegramId == telegramId, ct);
        
        string? adminIdStr = Environment.GetEnvironmentVariable("ADMIN_TG_ID")?.Trim('"', '\'', ' ');
        bool isAdmin = long.TryParse(adminIdStr, out long adminId) && telegramId == adminId;

        if (profile == null)
        {
            profile = new GameProfile
            {
                TelegramId = telegramId,
                CurrentEnergy = 50,
                LastEnergyUpdate = DateTime.UtcNow,
                BossKills = 0,
                LastDailyBonusDate = DateTime.MinValue
            };
            profile.EnergySignature = AntiCheatSigner.GenerateSignature(telegramId, profile.CurrentEnergy);
            _dbContext.GameProfiles.Add(profile);
            await _dbContext.SaveChangesAsync(ct);
        }
        else if (isAdmin)
        {
            if (profile.IsBanned)
            {
                profile.IsBanned = false;
                profile.BanReason = string.Empty;
                profile.EnergySignature = AntiCheatSigner.GenerateSignature(telegramId, profile.CurrentEnergy);
                await _dbContext.SaveChangesAsync(ct);
                _logger.LogInformation("Администратор {TelegramId} разблокирован.", telegramId);
            }
        }

        return profile;
    }

    public async Task<(bool Success, string Message, int RemainingEnergy)> TryStartGameAsync(long telegramId, CancellationToken ct)
    {
        var profile = await GetOrCreateProfileAsync(telegramId, ct);

        if (profile.IsBanned) return (false, "Аккаунт заблокирован за подозрительную активность.", 0);

        if (!AntiCheatSigner.VerifySignature(telegramId, profile.CurrentEnergy, profile.EnergySignature))
        {
            profile.IsBanned = true; profile.BanReason = "Нарушение целостности данных (Античит).";
            await _dbContext.SaveChangesAsync(ct);
            return (false, "Ошибка целостности данных.", 0);
        }

        if (profile.CurrentEnergy <= 0) return (false, "Недостаточно энергии! Подождите или пополните запас.", 0);

        // Оптимизированное обновление баланса через ExecuteUpdate (если нужно просто списать 1 энергию без трекинга)
        // Но здесь нам нужен объект профиля для возврата RemainingEnergy, поэтому SaveChanges уместен
        profile.CurrentEnergy -= 1;
        profile.CurrentGameStartTime = DateTime.UtcNow;
        profile.EnergySignature = AntiCheatSigner.GenerateSignature(telegramId, profile.CurrentEnergy);

        await _dbContext.SaveChangesAsync(ct);
        return (true, "Игра начата!", profile.CurrentEnergy);
    }

    public async Task<(bool Success, string Message)> SubmitScoreAsync(long telegramId, long score, CancellationToken ct)
    {
        var profile = await GetOrCreateProfileAsync(telegramId, ct);
        if (profile.IsBanned) return (false, "Аккаунт заблокирован.");

        string? adminIdStr = Environment.GetEnvironmentVariable("ADMIN_TG_ID")?.Trim('"', '\'', ' ');
        bool isAdmin = long.TryParse(adminIdStr, out long adminId) && telegramId == adminId;

        double elapsedSeconds = (DateTime.UtcNow - profile.CurrentGameStartTime).TotalSeconds;
        if (elapsedSeconds < 1) elapsedSeconds = 1;

        if (!isAdmin && (score / elapsedSeconds) > 150)
        {
            profile.IsBanned = true;
            profile.BanReason = "SpeedHack detected (TimeLock Limit Exceeded).";
            await _dbContext.SaveChangesAsync(ct);
            return (false, "Обнаружена аномальная скорость. Аккаунт заблокирован.");
        }

        var record = await _dbContext.LeaderboardRecords.FirstOrDefaultAsync(r => r.TelegramId == telegramId, ct);

        if (record == null)
        {
            record = new LeaderboardRecord { TelegramId = telegramId, MaxScore = score, AchievedAt = DateTime.UtcNow, ScoreSignature = AntiCheatSigner.GenerateSignature(telegramId, score) };
            _dbContext.LeaderboardRecords.Add(record);
        }
        else if (score > record.MaxScore)
        {
            if (!AntiCheatSigner.VerifySignature(telegramId, record.MaxScore, record.ScoreSignature))
            {
                profile.IsBanned = true; profile.BanReason = "Повреждение рекорда (Античит).";
                await _dbContext.SaveChangesAsync(ct); return (false, "Ошибка целостности данных.");
            }
            record.MaxScore = score; record.AchievedAt = DateTime.UtcNow; record.ScoreSignature = AntiCheatSigner.GenerateSignature(telegramId, score);
        }
        await _dbContext.SaveChangesAsync(ct); return (true, "Результат сохранен!");
    }

    public async Task<(bool Success, string Message)> ProcessBossVictoryAsync(long telegramId, CancellationToken ct)
    {
        var profile = await GetOrCreateProfileAsync(telegramId, ct);
        if (profile.IsBanned) return (false, "Аккаунт заблокирован.");

        string? adminIdStr = Environment.GetEnvironmentVariable("ADMIN_TG_ID")?.Trim('"', '\'', ' ');
        bool isAdmin = long.TryParse(adminIdStr, out long adminId) && telegramId == adminId;

        double elapsedSeconds = (DateTime.UtcNow - profile.CurrentGameStartTime).TotalSeconds;
        if (!isAdmin && elapsedSeconds < 19)
        {
            profile.IsBanned = true;
            profile.BanReason = "Boss SpeedHack (TimeLock Violation).";
            await _dbContext.SaveChangesAsync(ct);
            return (false, "Аномальное время победы. Аккаунт заблокирован.");
        }

        profile.BossKills += 1;
        profile.MonthlyBossKills += 1;
        profile.LastBossKillDate = DateTime.UtcNow;

        // ВАЖНО: Подписки лежат в другом контексте. Мы можем внедрить VpnDbContext сюда или вызвать другой сервис.
        // Для простоты оставим логику расширения подписки, но используем raw SQL или отдельный context.
        await _dbContext.Database.ExecuteSqlRawAsync(
            "UPDATE VpnSubscriptions SET ExpiryDate = datetime(ExpiryDate, '+7 days'), SyncStatus = 2 WHERE TelegramId = {0} AND IsActive = 1", 
            telegramId);

        await _dbContext.SaveChangesAsync(ct);
        return (true, "Босс побежден! Начислено 7 дней доступа.");
    }

    public async Task<(bool Success, string Message, int NewEnergy)> AddCheatEnergyAsync(long telegramId, int amount, CancellationToken ct)
    {
        var profile = await GetOrCreateProfileAsync(telegramId, ct);
        profile.CurrentEnergy += amount;
        profile.EnergySignature = AntiCheatSigner.GenerateSignature(telegramId, profile.CurrentEnergy);
        await _dbContext.SaveChangesAsync(ct);
        return (true, "Режим бога активирован.", profile.CurrentEnergy);
    }

    public async Task<(bool Success, string Message)> ResetBossStatsAsync(long telegramId, CancellationToken ct)
    {
        var profile = await GetOrCreateProfileAsync(telegramId, ct);
        profile.BossKills = 0;
        profile.MonthlyBossKills = 0;
        profile.LastBossKillDate = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        await _dbContext.SaveChangesAsync(ct);
        return (true, "Счетчик боссов сброшен на 0!");
    }

    public async Task<(bool Success, string Message, int NewEnergy)> ClaimDailyBonusAsync(long telegramId, CancellationToken ct)
    {
        var profile = await GetOrCreateProfileAsync(telegramId, ct);

        if (profile.IsBanned)
            return (false, "Аккаунт заблокирован.", profile.CurrentEnergy);

        if (profile.LastDailyBonusDate.Date >= DateTime.UtcNow.Date)
            return (false, "Вы уже получали бонус сегодня. Возвращайтесь завтра!", profile.CurrentEnergy);

        profile.CurrentEnergy += 5;
        profile.LastDailyBonusDate = DateTime.UtcNow;
        profile.EnergySignature = AntiCheatSigner.GenerateSignature(telegramId, profile.CurrentEnergy);

        await _dbContext.SaveChangesAsync(ct);
        return (true, "Ежедневный бонус +5 ⚡ успешно начислен!", profile.CurrentEnergy);
    }

    public async Task<object> GetLeaderboardAsync(CancellationToken ct)
    {
        var topRecords = await _dbContext.LeaderboardRecords
            .OrderByDescending(r => r.MaxScore)
            .Take(10)
            .Join(_dbContext.TelegramUsers,
                  record => record.TelegramId,
                  user => user.TelegramId,
                  (record, user) => new {
                      Name = string.IsNullOrEmpty(user.FirstName) ? "Аноним" : user.FirstName,
                      record.MaxScore
                  })
            .ToListAsync(ct);

        return new { TopPlayers = topRecords };
    }
}
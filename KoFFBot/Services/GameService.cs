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
    private readonly VpnDbContext _vpnDbContext;
    private readonly ILogger<GameService> _logger;

    public GameService(GameDbContext dbContext, VpnDbContext vpnDbContext, ILogger<GameService> logger)
    {
        _dbContext = dbContext;
        _vpnDbContext = vpnDbContext;
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
        else
        {
            // === AUTO-REPAIR: Исправляем последствия бага с рассинхроном сигнатуры ===
            if (profile.IsBanned && profile.BanReason == "Нарушение целостности данных (Античит).")
            {
                profile.IsBanned = false;
                profile.BanReason = string.Empty;
                profile.EnergySignature = AntiCheatSigner.GenerateSignature(telegramId, profile.CurrentEnergy);
                await _dbContext.SaveChangesAsync(ct);
                _logger.LogInformation("Профиль {TelegramId} успешно восстановлен после сбоя античита.", telegramId);
            }

            if (isAdmin && profile.IsBanned)
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

    public async Task<(bool Success, string Message, int NewEnergy)> AddEnergyAsync(long telegramId, int amount, CancellationToken ct)
    {
        var profile = await GetOrCreateProfileAsync(telegramId, ct);
        profile.CurrentEnergy += amount;
        profile.EnergySignature = AntiCheatSigner.GenerateSignature(telegramId, profile.CurrentEnergy);
        await _dbContext.SaveChangesAsync(ct);
        return (true, $"Начислено +{amount} энергии.", profile.CurrentEnergy);
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

    public async Task<(bool Success, string Message, int? NewEnergy)> ProcessBossVictoryAsync(long telegramId, CancellationToken ct)
    {
        var profile = await GetOrCreateProfileAsync(telegramId, ct);
        if (profile.IsBanned) return (false, "Аккаунт заблокирован.", null);

        string? adminIdStr = Environment.GetEnvironmentVariable("ADMIN_TG_ID")?.Trim('"', '\'', ' ');
        bool isAdmin = long.TryParse(adminIdStr, out long adminId) && telegramId == adminId;

        double elapsedSeconds = (DateTime.UtcNow - profile.CurrentGameStartTime).TotalSeconds;
        if (!isAdmin && elapsedSeconds < 19)
        {
            profile.IsBanned = true;
            profile.BanReason = "Boss SpeedHack (TimeLock Violation).";
            await _dbContext.SaveChangesAsync(ct);
            return (false, "Аномальное время победы. Аккаунт заблокирован.", null);
        }

        profile.BossKills += 1;
        profile.MonthlyBossKills += 1;
        profile.LastBossKillDate = DateTime.UtcNow;

        var random = new Random();
        int rewardRoll = random.Next(1, 101); // 1-100
        int? newEnergy = null;
        string rewardMessage = "";

        if (rewardRoll <= 5) // 5% chance for key
        {
            int keyRoll = random.Next(1, 100);
            int addedDays = keyRoll <= 60 ? 1 : (keyRoll <= 90 ? 2 : 3);

            var vpnSub = await _vpnDbContext.VpnSubscriptions
                .FirstOrDefaultAsync(s => s.TelegramId == telegramId && s.IsActive, ct);

            if (vpnSub != null)
            {
                DateTime baseDate = (vpnSub.ExpiryDate.HasValue && vpnSub.ExpiryDate.Value > DateTime.UtcNow)
                    ? vpnSub.ExpiryDate.Value
                    : DateTime.UtcNow;

                await _vpnDbContext.VpnSubscriptions
                    .Where(s => s.Uuid == vpnSub.Uuid)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(s => s.ExpiryDate, baseDate.AddDays(addedDays))
                        .SetProperty(s => s.SyncStatus, SyncStatus.PendingUpdate)
                        .SetProperty(s => s.LastModifiedAt, DateTime.UtcNow), ct);
            }
            else
            {
                var template = await _vpnDbContext.ServerTemplates.FirstOrDefaultAsync(ct);
                if (template != null)
                {
                    _vpnDbContext.VpnSubscriptions.Add(new VpnSubscription
                    {
                        Uuid = Guid.NewGuid().ToString(),
                        TelegramId = telegramId,
                        Email = $"boss_win_{telegramId}",
                        ServerIp = template.ServerIp,
                        TrafficLimitBytes = 50L * 1024 * 1024 * 1024,
                        IsActive = true,
                        ExpiryDate = DateTime.UtcNow.AddDays(addedDays),
                        SyncStatus = SyncStatus.PendingAdd,
                        LastModifiedAt = DateTime.UtcNow
                    });
                }
            }
            await _vpnDbContext.SaveChangesAsync(ct);
            rewardMessage = $"Босс побежден! Получено продление на {addedDays} дн.";
        }
        else // 95% chance for energy
        {
            int energyRoll = random.Next(1, 100);
            int addedEnergy = 1;
            if (energyRoll <= 30) addedEnergy = 1;
            else if (energyRoll <= 55) addedEnergy = 2;
            else if (energyRoll <= 75) addedEnergy = 3;
            else if (energyRoll <= 88) addedEnergy = 5;
            else if (energyRoll <= 96) addedEnergy = 10;
            else addedEnergy = 25;

            profile.CurrentEnergy += addedEnergy;
            profile.EnergySignature = AntiCheatSigner.GenerateSignature(telegramId, profile.CurrentEnergy);
            newEnergy = profile.CurrentEnergy;
            rewardMessage = $"Босс побежден! Найдено +{addedEnergy} ⚡";
        }

        await _dbContext.SaveChangesAsync(ct);
        return (true, rewardMessage, newEnergy);
    }

    public async Task<(bool Success, string Message, int? NewEnergy)> ProcessChestCollectionAsync(long telegramId, long score, CancellationToken ct)
    {
        var profile = await GetOrCreateProfileAsync(telegramId, ct);
        if (profile.IsBanned) return (false, "Аккаунт заблокирован.", null);

        string? adminIdStr = Environment.GetEnvironmentVariable("ADMIN_TG_ID")?.Trim('"', '\'', ' ');
        bool isAdmin = long.TryParse(adminIdStr, out long adminId) && telegramId == adminId;

        // Anti-cheat validations
        if (!isAdmin && score < 190)
        {
            profile.IsBanned = true;
            profile.BanReason = "Chest Anti-Cheat (Score manipulation).";
            await _dbContext.SaveChangesAsync(ct);
            return (false, "Слишком мало очков для сундука. Аккаунт заблокирован.", null);
        }

        // Zero-Trust: allow only 1 chest per game start session.
        if (profile.LastChestGameTime == profile.CurrentGameStartTime)
        {
            return (false, "Сундук уже собран в этой игре.", null);
        }
        profile.LastChestGameTime = profile.CurrentGameStartTime;

        var random = new Random();
        int energyRoll = random.Next(1, 100);
        int addedEnergy = 1;
        if (energyRoll <= 35) addedEnergy = 1;
        else if (energyRoll <= 65) addedEnergy = 2;
        else if (energyRoll <= 85) addedEnergy = 3;
        else if (energyRoll <= 95) addedEnergy = 5;
        else addedEnergy = 10;

        profile.CurrentEnergy += addedEnergy;
        profile.EnergySignature = AntiCheatSigner.GenerateSignature(telegramId, profile.CurrentEnergy);
        
        await _dbContext.SaveChangesAsync(ct);
        return (true, $"Найден сундук! +{addedEnergy} ⚡", profile.CurrentEnergy);
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

    public async Task<(bool Success, string Message, int NewEnergy)> ClaimAdvancedBonusAsync(long telegramId, string bonusType, CancellationToken ct)
    {
        var profile = await GetOrCreateProfileAsync(telegramId, ct);
        if (profile.IsBanned) return (false, "Аккаунт заблокирован.", profile.CurrentEnergy);

        int reward = 0;
        string message = "";

        if (bonusType.StartsWith("REF_"))
        {
            // Рубежи: 1, 3, 5, 10 друзей -> +20 энергии
            if (!int.TryParse(bonusType.Replace("REF_", ""), out int milestone))
                return (false, "Неверный тип бонуса.", profile.CurrentEnergy);

            var user = await _dbContext.TelegramUsers.FirstOrDefaultAsync(u => u.TelegramId == telegramId, ct);
            if (user == null || user.ReferralCount < milestone)
                return (false, "Условие рубежа не выполнено.", profile.CurrentEnergy);

            if (profile.ClaimedReferralMilestone >= milestone)
                return (false, "Этот бонус уже получен.", profile.CurrentEnergy);

            reward = 20;
            profile.ClaimedReferralMilestone = milestone;
            message = $"Бонус за {milestone} друзей (+{reward} ⚡) начислен!";
        }
        else if (bonusType == "HAPPY_HOUR")
        {
            var now = DateTime.UtcNow;
            if (!((now.DayOfWeek == DayOfWeek.Tuesday || now.DayOfWeek == DayOfWeek.Friday) && (now.Hour >= 18 && now.Hour < 20)))
                return (false, "Счастливые часы сейчас не активны.", profile.CurrentEnergy);

            if (profile.LastHappyHourDate.Date == now.Date)
                return (false, "Вы уже получили бонус счастливого часа сегодня.", profile.CurrentEnergy);

            reward = 5;
            profile.LastHappyHourDate = now;
            message = "Бонус счастливого часа (+5 ⚡) начислен!";
        }
        else if (bonusType == "RETENTION")
        {
            if (profile.LastRetentionDate.Date == DateTime.UtcNow.Date)
                return (false, "Вы уже получили бонус за удержание сегодня.", profile.CurrentEnergy);

            reward = 5;
            profile.LastRetentionDate = DateTime.UtcNow;
            message = "Бонус за лояльность (+5 ⚡) начислен!";
        }
        else
        {
            return (false, "Неизвестный тип бонуса.", profile.CurrentEnergy);
        }

        profile.CurrentEnergy += reward;
        profile.EnergySignature = AntiCheatSigner.GenerateSignature(telegramId, profile.CurrentEnergy);
        await _dbContext.SaveChangesAsync(ct);

        return (true, message, profile.CurrentEnergy);
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


using KoFFBot.Data;
using KoFFBot.Domain;
using KoFFBot.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace KoFFBot.Services;

public class GameService
{
    private readonly BotDbContext _dbContext;
    private readonly ILogger<GameService> _logger;

    public GameService(BotDbContext dbContext, ILogger<GameService> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    // 1. Запрос профиля игрока (и создание, если его нет)
    public async Task<GameProfile> GetOrCreateProfileAsync(long telegramId, CancellationToken ct)
    {
        var profile = await _dbContext.GameProfiles.FirstOrDefaultAsync(p => p.TelegramId == telegramId, ct);

        if (profile == null)
        {
            profile = new GameProfile
            {
                TelegramId = telegramId,
                CurrentEnergy = 5,
                LastEnergyUpdate = DateTime.UtcNow
            };

            // Генерируем начальную подпись античита
            profile.EnergySignature = AntiCheatSigner.GenerateSignature(telegramId, profile.CurrentEnergy);

            _dbContext.GameProfiles.Add(profile);
            await _dbContext.SaveChangesAsync(ct);
        }

        return profile;
    }

    // 2. Попытка начать игру (Списание энергии)
    public async Task<(bool Success, string Message, int RemainingEnergy)> TryStartGameAsync(long telegramId, CancellationToken ct)
    {
        var profile = await GetOrCreateProfileAsync(telegramId, ct);

        if (profile.IsBanned)
            return (false, "Аккаунт заблокирован за подозрительную активность.", 0);

        // Проверка Античита
        if (!AntiCheatSigner.VerifySignature(telegramId, profile.CurrentEnergy, profile.EnergySignature))
        {
            profile.IsBanned = true;
            profile.BanReason = "Нарушение целостности данных (Античит).";
            await _dbContext.SaveChangesAsync(ct);
            _logger.LogWarning($"[АНТИЧИТ] Игрок {telegramId} забанен за подделку энергии!");
            return (false, "Ошибка целостности данных.", 0);
        }

        if (profile.CurrentEnergy <= 0)
            return (false, "Недостаточно энергии! Подождите или пополните запас.", 0);

        // Списываем энергию
        profile.CurrentEnergy -= 1;
        profile.EnergySignature = AntiCheatSigner.GenerateSignature(telegramId, profile.CurrentEnergy);

        await _dbContext.SaveChangesAsync(ct);
        return (true, "Игра начата!", profile.CurrentEnergy);
    }

    // 3. Обработка результатов игры (Сохранение рекорда)
    public async Task<(bool Success, string Message)> SubmitScoreAsync(long telegramId, long score, string clientSignature, CancellationToken ct)
    {
        var profile = await GetOrCreateProfileAsync(telegramId, ct);

        if (profile.IsBanned) return (false, "Аккаунт заблокирован.");

        // Проверяем подпись, которую прислал клиент (фронтенд)
        if (!AntiCheatSigner.VerifySignature(telegramId, score, clientSignature))
        {
            _logger.LogWarning($"[АНТИЧИТ] Попытка подделки очков от {telegramId}. Заявлено: {score}");
            return (false, "Ошибка синхронизации данных.");
        }

        var record = await _dbContext.LeaderboardRecords.FirstOrDefaultAsync(r => r.TelegramId == telegramId, ct);

        if (record == null)
        {
            record = new LeaderboardRecord
            {
                TelegramId = telegramId,
                MaxScore = score,
                AchievedAt = DateTime.UtcNow,
                ScoreSignature = AntiCheatSigner.GenerateSignature(telegramId, score)
            };
            _dbContext.LeaderboardRecords.Add(record);
        }
        else if (score > record.MaxScore)
        {
            // Проверка целостности старого рекорда перед обновлением
            if (!AntiCheatSigner.VerifySignature(telegramId, record.MaxScore, record.ScoreSignature))
            {
                profile.IsBanned = true;
                profile.BanReason = "Повреждение рекорда (Античит).";
                await _dbContext.SaveChangesAsync(ct);
                return (false, "Ошибка целостности данных.");
            }

            record.MaxScore = score;
            record.AchievedAt = DateTime.UtcNow;
            record.ScoreSignature = AntiCheatSigner.GenerateSignature(telegramId, score);
        }

        await _dbContext.SaveChangesAsync(ct);
        return (true, "Результат сохранен!");
    }

    // 4. Обработка победы над Боссом (Выдача приза)
    public async Task<(bool Success, string Message)> ProcessBossVictoryAsync(long telegramId, string clientSignature, CancellationToken ct)
    {
        var profile = await GetOrCreateProfileAsync(telegramId, ct);
        if (profile.IsBanned) return (false, "Аккаунт заблокирован.");

        // Секретный код для босса (1000 - условное значение победы над боссом)
        if (!AntiCheatSigner.VerifySignature(telegramId, 1000, clientSignature))
        {
            _logger.LogWarning($"[АНТИЧИТ] Попытка подделки победы над боссом от {telegramId}.");
            return (false, "Неверный код победы.");
        }

        // Начисляем приз (7 дней)
        var sub = await _dbContext.VpnSubscriptions.FirstOrDefaultAsync(s => s.TelegramId == telegramId && s.IsActive, ct);
        if (sub != null)
        {
            DateTime baseDate = (sub.ExpiryDate.HasValue && sub.ExpiryDate.Value > DateTime.UtcNow) ? sub.ExpiryDate.Value : DateTime.UtcNow;
            sub.ExpiryDate = baseDate.AddDays(7);
            sub.SyncStatus = SyncStatus.PendingUpdate;
            await _dbContext.SaveChangesAsync(ct);

            _logger.LogInformation($"Игрок {telegramId} победил босса! Добавлено 7 дней.");
            return (true, "Босс побежден! Начислено 7 дней доступа.");
        }

        return (false, "Активный ключ не найден. Сначала запустите туннель.");
    }

    // === НОВЫЙ МЕТОД: 5. Режим Разработчика (Выдача энергии) ===
    public async Task<(bool Success, string Message, int NewEnergy)> AddCheatEnergyAsync(long telegramId, int amount, CancellationToken ct)
    {
        var profile = await GetOrCreateProfileAsync(telegramId, ct);

        profile.CurrentEnergy += amount;

        // Обязательно обновляем крипто-подпись, иначе античит нас забанит
        profile.EnergySignature = AntiCheatSigner.GenerateSignature(telegramId, profile.CurrentEnergy);

        await _dbContext.SaveChangesAsync(ct);
        _logger.LogInformation($"[DEV-MODE] Игроку (Админу) {telegramId} выдано {amount} энергии.");

        return (true, "Режим бога активирован.", profile.CurrentEnergy);
    }
}
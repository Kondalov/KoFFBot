using KoFFBot.Data;
using KoFFBot.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace KoFFBot.Services;

public class SubscriptionGenerator
{
    private readonly BotDbContext _dbContext;
    private readonly ILogger<SubscriptionGenerator> _logger;

    public SubscriptionGenerator(BotDbContext dbContext, ILogger<SubscriptionGenerator> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    public async Task<(bool IsSuccess, string? Uuid, string? ServerIp, string? ErrorMessage)> GenerateNewSubscriptionAsync(
        long telegramId, string email, CancellationToken ct)
    {
        try
        {
            // УМНЫЙ АЛГОРИТМ: Ищем свободный ключ в резервном пуле (TelegramId == 0)
            var reserveSub = await _dbContext.VpnSubscriptions
                .FirstOrDefaultAsync(s => s.TelegramId == 0 && s.IsActive, ct);

            if (reserveSub == null)
            {
                _logger.LogWarning("Закончились резервные ключи в пуле!");
                return (false, null, null, "⏳ Нет доступных серверов резерва. Обратитесь к администратору.");
            }

            // Бронируем ключ за пользователем
            reserveSub.TelegramId = telegramId;
            reserveSub.Email = email;

            // === ИСПРАВЛЕНИЕ АРХИТЕКТУРЫ ===
            // Обновляем дату! Тестовый период (например, 3 дня) начинается ИМЕННО СЕЙЧАС.
            reserveSub.ExpiryDate = DateTime.UtcNow.AddDays(3);

            // Ставим статус PendingUpdate, чтобы панель на ПК забрала этот ключ из пула в активные!
            reserveSub.SyncStatus = SyncStatus.PendingUpdate;
            reserveSub.LastModifiedAt = DateTime.UtcNow;

            await _dbContext.SaveChangesAsync(ct);

            _logger.LogInformation($"Выдан резервный ключ (UUID: {reserveSub.Uuid}) для ID: {telegramId}. Срок установлен до {reserveSub.ExpiryDate:dd.MM.yyyy}");
            return (true, reserveSub.Uuid, reserveSub.ServerIp, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка при выдаче резервного ключа.");
            return (false, null, null, "Внутренняя ошибка. Попробуйте позже.");
        }
    }
}
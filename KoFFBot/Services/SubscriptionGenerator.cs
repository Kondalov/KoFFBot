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
    private readonly VpnDbContext _dbContext;
    private readonly ILogger<SubscriptionGenerator> _logger;

    public SubscriptionGenerator(VpnDbContext dbContext, ILogger<SubscriptionGenerator> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    public async Task<(bool IsSuccess, string? Uuid, string? ServerIp, string? ErrorMessage)> GenerateNewSubscriptionAsync(
        long telegramId, string email, CancellationToken ct)
    {
        try
        {
            var reserveSub = await _dbContext.VpnSubscriptions
                .FirstOrDefaultAsync(s => s.TelegramId == 0 && s.IsActive, ct);

            if (reserveSub == null)
            {
                _logger.LogWarning("Закончились резервные ключи в пуле!");
                return (false, null, null, "⏳ Нет доступных серверов резерва. Обратитесь к администратору.");
            }

            reserveSub.TelegramId = telegramId;
            reserveSub.Email = email;
            reserveSub.ExpiryDate = DateTime.UtcNow.AddDays(3);
            reserveSub.SyncStatus = SyncStatus.PendingUpdate;
            reserveSub.LastModifiedAt = DateTime.UtcNow;

            await _dbContext.SaveChangesAsync(ct);

            _logger.LogInformation("Выдан резервный ключ (UUID: {Uuid}) для ID: {TelegramId}. Срок: {ExpiryDate:dd.MM.yyyy}", reserveSub.Uuid, telegramId, reserveSub.ExpiryDate);
            return (true, reserveSub.Uuid, reserveSub.ServerIp, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка при выдаче резервного ключа.");
            return (false, null, null, "Внутренняя ошибка. Попробуйте позже.");
        }
    }
}